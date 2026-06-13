namespace OpenPlanTrace;

public static class LayerAnalyzer
{
    public const string UnlayeredName = "(unlayered)";

    private static readonly IReadOnlyDictionary<LayerCategory, string[]> NameHints =
        new Dictionary<LayerCategory, string[]>
        {
            [LayerCategory.Wall] = new[] { "wall", "walls", "a-wall", "vegg", "vegger", "mur", "partition" },
            [LayerCategory.Door] = new[] { "door", "doors", "a-door", "dor", "dor", "dorer", "deur" },
            [LayerCategory.Window] = new[] { "window", "windows", "a-window", "vindu", "vinduer", "glass" },
            [LayerCategory.Room] = new[] { "room", "rooms", "space", "spaces", "rom", "area" },
            [LayerCategory.Dimension] = new[] { "dim", "dims", "dimension", "dimensions", "maal", "maal", "measure" },
            [LayerCategory.Text] = new[] { "text", "txt", "anno", "annot", "annotation", "note", "notes" },
            [LayerCategory.Grid] = new[] { "grid", "axis", "axes", "akse", "akser", "module" },
            [LayerCategory.Structural] = new[] { "struct", "structural", "beam", "column", "col", "baering", "baering", "steel", "concrete" },
            [LayerCategory.Equipment] = new[] { "equip", "equipment", "utstyr", "machine", "maskin", "process" },
            [LayerCategory.Electrical] = new[] { "elec", "electrical", "power", "kraft", "lighting", "lys", "el-" },
            [LayerCategory.HVAC] = new[] { "hvac", "vent", "ventilation", "duct", "kanal", "air", "luft" },
            [LayerCategory.Plumbing] = new[] { "plumb", "plumbing", "vvs", "pipe", "pipes", "ror", "ror", "water", "drain" },
            [LayerCategory.FireSafety] = new[] { "fire", "brann", "sprinkler", "alarm", "escape", "evac" }
        };

    public static PlanLayerAnalysis Analyze(
        PlanDocument document,
        IReadOnlyList<LayerCategoryOverride>? categoryOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        categoryOverrides ??= Array.Empty<LayerCategoryOverride>();

        var layerItems = document.Pages
            .SelectMany(page => page.Primitives.Select((primitive, index) => new LayerPrimitive(page.Number, index, primitive)))
            .GroupBy(item => new LayerKey(LayerName(item.Primitive), SourceFormat(item.Primitive)), LayerKeyComparer.Instance)
            .Select(group => CreateSummary(group, categoryOverrides))
            .OrderBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(summary => summary.SourceFormat)
            .ToArray();

        return new PlanLayerAnalysis(layerItems);
    }

    private static LayerSummary CreateSummary(
        IGrouping<LayerKey, LayerPrimitive> group,
        IReadOnlyList<LayerCategoryOverride> categoryOverrides)
    {
        var primitives = group.ToArray();
        var kindCounts = primitives
            .GroupBy(item => item.Primitive.Kind)
            .ToDictionary(item => item.Key, item => item.Count());
        var totalLineLength = primitives.Sum(item => PrimitiveLength(item.Primitive));
        var bounds = PlanRect.Union(primitives.Select(item => item.Primitive.Bounds));
        var pageNumbers = primitives.Select(item => item.PageNumber).Distinct().Order().ToArray();
        var classification = Classify(group.Key.Name, group.Key.SourceFormat, primitives, kindCounts, totalLineLength, categoryOverrides);

        return new LayerSummary(
            group.Key.Name,
            group.Key.SourceFormat,
            primitives.Length,
            kindCounts,
            totalLineLength,
            bounds,
            classification.Category,
            classification.Confidence,
            classification.CategoryScores,
            classification.Evidence,
            pageNumbers);
    }

    private static LayerClassification Classify(
        string layerName,
        string? sourceFormat,
        IReadOnlyList<LayerPrimitive> primitives,
        IReadOnlyDictionary<PlanPrimitiveKind, int> kindCounts,
        double totalLineLength,
        IReadOnlyList<LayerCategoryOverride> categoryOverrides)
    {
        var layerOverride = categoryOverrides.FirstOrDefault(overrideRule => overrideRule.Matches(layerName, sourceFormat));
        if (layerOverride is not null)
        {
            var sourceEvidence = string.IsNullOrWhiteSpace(layerOverride.SourceFormat)
                ? string.Empty
                : $" for source format '{layerOverride.SourceFormat}'";
            return new LayerClassification(
                layerOverride.Category,
                Confidence.High,
                new[]
                {
                    new LayerCategoryScore(
                        layerOverride.Category,
                        Confidence.High.Value,
                        new[] { $"layer category override matched '{layerOverride.Pattern}'{sourceEvidence}" })
                },
                new[] { $"layer category override matched '{layerOverride.Pattern}'{sourceEvidence}" });
        }

        var scores = Enum.GetValues<LayerCategory>()
            .Where(category => category != LayerCategory.Unknown)
            .ToDictionary(category => category, _ => 0.0);
        var evidence = new Dictionary<LayerCategory, List<string>>();

        foreach (var category in scores.Keys)
        {
            evidence[category] = new List<string>();
        }

        var normalizedName = Normalize(layerName);
        foreach (var (category, hints) in NameHints)
        {
            foreach (var hint in hints)
            {
                if (ContainsTokenish(normalizedName, Normalize(hint)))
                {
                    scores[category] += 0.72;
                    evidence[category].Add($"layer name matches '{hint}'");
                    break;
                }
            }
        }

        var entityCount = Math.Max(1, primitives.Count);
        var textCount = kindCounts.GetValueOrDefault(PlanPrimitiveKind.Text);
        var lineishCount = kindCounts.GetValueOrDefault(PlanPrimitiveKind.Line)
            + kindCounts.GetValueOrDefault(PlanPrimitiveKind.Polyline)
            + kindCounts.GetValueOrDefault(PlanPrimitiveKind.Rectangle)
            + kindCounts.GetValueOrDefault(PlanPrimitiveKind.Arc);
        var symbolCount = kindCounts.GetValueOrDefault(PlanPrimitiveKind.Symbol);

        if (textCount >= entityCount * 0.65)
        {
            scores[LayerCategory.Text] += 0.28;
            evidence[LayerCategory.Text].Add("mostly text primitives");
        }

        if (lineishCount >= entityCount * 0.75 && totalLineLength > 80)
        {
            scores[LayerCategory.Wall] += 0.18;
            evidence[LayerCategory.Wall].Add("mostly long linework");
        }

        if (kindCounts.GetValueOrDefault(PlanPrimitiveKind.Arc) > 0)
        {
            scores[LayerCategory.Door] += 0.18;
            evidence[LayerCategory.Door].Add("contains arc primitives");
        }

        if (symbolCount > 0)
        {
            scores[LayerCategory.Equipment] += 0.16;
            evidence[LayerCategory.Equipment].Add("contains symbol/block primitives");
        }

        var dimensionTextCount = primitives.Count(item => item.Primitive is TextPrimitive text && LooksLikeDimensionText(text.Text));
        if (dimensionTextCount > 0)
        {
            scores[LayerCategory.Dimension] += Math.Min(0.36, 0.12 * dimensionTextCount);
            evidence[LayerCategory.Dimension].Add("contains dimension-like text");
        }

        var categoryScores = scores
            .Where(item => item.Value > 0)
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key)
            .Select(item => new LayerCategoryScore(
                item.Key,
                Math.Min(0.95, item.Value),
                evidence[item.Key].Count == 0 ? new[] { "geometry evidence" } : evidence[item.Key].ToArray()))
            .ToArray();
        var top = scores
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key)
            .First();

        if (top.Value < 0.24)
        {
            return new LayerClassification(
                LayerCategory.Unknown,
                Confidence.Low,
                categoryScores,
                new[] { "no strong layer name or geometry evidence" });
        }

        return new LayerClassification(
            top.Key,
            new Confidence(Math.Min(0.95, top.Value)),
            categoryScores,
            evidence[top.Key].Count == 0 ? new[] { "geometry evidence" } : evidence[top.Key].ToArray());
    }

    private static string LayerName(PlanPrimitive primitive) =>
        Clean(primitive.Source.Layer)
        ?? Clean(primitive.Layer)
        ?? UnlayeredName;

    private static string? SourceFormat(PlanPrimitive primitive) =>
        Clean(primitive.Source.SourceFormat);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ContainsTokenish(string text, string hint)
    {
        if (hint.Length == 0)
        {
            return false;
        }

        return text.Equals(hint, StringComparison.Ordinal)
            || text.Contains($"-{hint}-", StringComparison.Ordinal)
            || text.StartsWith($"{hint}-", StringComparison.Ordinal)
            || text.EndsWith($"-{hint}", StringComparison.Ordinal)
            || text.Contains($"_{hint}_", StringComparison.Ordinal)
            || text.StartsWith($"{hint}_", StringComparison.Ordinal)
            || text.EndsWith($"_{hint}", StringComparison.Ordinal)
            || text.Contains(hint, StringComparison.Ordinal);
    }

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant().Replace('\\', '-').Replace('/', '-').Replace('.', '-').Replace(' ', '-');

    private static double PrimitiveLength(PlanPrimitive primitive) =>
        primitive switch
        {
            LinePrimitive line => line.Segment.Length,
            RectanglePrimitive rectangle => rectangle.Rectangle.IsEmpty ? 0 : (rectangle.Rectangle.Width * 2) + (rectangle.Rectangle.Height * 2),
            PolylinePrimitive polyline => PolylineLength(polyline),
            ArcPrimitive arc => Math.Abs(arc.SweepAngleRadians) * arc.Radius,
            _ => 0
        };

    private static double PolylineLength(PolylinePrimitive polyline)
    {
        if (polyline.Points.Count < 2)
        {
            return 0;
        }

        var length = 0.0;
        for (var index = 1; index < polyline.Points.Count; index++)
        {
            length += polyline.Points[index - 1].DistanceTo(polyline.Points[index]);
        }

        if (polyline.Closed)
        {
            length += polyline.Points[^1].DistanceTo(polyline.Points[0]);
        }

        return length;
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
                || trimmed.Contains('X')
                || trimmed.Contains(':'));
    }

    private sealed record LayerPrimitive(int PageNumber, int PrimitiveIndex, PlanPrimitive Primitive);

    private sealed record LayerKey(string Name, string? SourceFormat);

    private sealed record LayerClassification(
        LayerCategory Category,
        Confidence Confidence,
        IReadOnlyList<LayerCategoryScore> CategoryScores,
        IReadOnlyList<string> Evidence);

    private sealed class LayerKeyComparer : IEqualityComparer<LayerKey>
    {
        public static LayerKeyComparer Instance { get; } = new();

        public bool Equals(LayerKey? left, LayerKey? right) =>
            left is not null
            && right is not null
            && string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.SourceFormat, right.SourceFormat, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(LayerKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name),
                obj.SourceFormat is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SourceFormat));
    }
}
