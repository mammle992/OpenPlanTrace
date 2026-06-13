using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenPlanTrace;

public static class PlanCalibrationAnalyzer
{
    private const double PdfPointsPerInch = 72.0;
    private const double MillimetersPerInch = 25.4;

    private static readonly Regex RatioScaleRegex =
        new(@"(?i)\b1\s*:\s*(?<ratio>\d+(?:[\.,]\d+)?)\b", RegexOptions.Compiled);

    private static readonly Regex ImperialScaleRegex =
        new(@"(?i)(?<paper>\d+(?:[\.,]\d+)?|\d+\s*/\s*\d+)\s*(?:""|in|inch|inches)\s*=\s*(?<feet>\d+(?:[\.,]\d+)?)\s*(?:'|ft|foot|feet)(?:\s*-?\s*(?<inches>\d+(?:[\.,]\d+)?|\d+\s*/\s*\d+)?\s*(?:""|in|inch|inches)?)?", RegexOptions.Compiled);

    private static readonly Regex MetricDimensionRegex =
        new(@"(?i)(?<![A-Za-z])(?<value>\d+(?:[\.,]\d+)?)\s*(?<unit>mm|cm|m)\b", RegexOptions.Compiled);

    private static readonly Regex MetricWholeMillimeterDimensionRegex =
        new(@"(?i)^\s*(?<value>\d{1,3}(?:\s+\d{3})+)(?:\s*mm)?\s*$", RegexOptions.Compiled);

    private static readonly Regex AreaMeasurementRegex =
        new(@"(?i)\b\d+(?:[\.,]\d+)?\s*m(?:2|\^2|²|㎡)\b", RegexOptions.Compiled);

    private static readonly Regex FeetDimensionRegex =
        new(@"(?i)(?<feet>\d+(?:[\.,]\d+)?)\s*'\s*(?:-?\s*(?<inches>\d+(?:[\.,]\d+)?|\d+\s*/\s*\d+)?)?\s*(?:""|in|inch|inches)?", RegexOptions.Compiled);

    private static readonly Regex InchesDimensionRegex =
        new(@"(?i)(?<inches>\d+(?:[\.,]\d+)?|\d+\s*/\s*\d+)\s*(?:""|in|inch|inches)\b", RegexOptions.Compiled);

    public static PlanCalibration Analyze(
        PlanDocument document,
        IReadOnlyList<SheetRegion>? sheetRegions = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        sheetRegions ??= Array.Empty<SheetRegion>();

        var evidence = new List<CalibrationEvidence>();
        var candidates = new List<CalibrationCandidate>();

        AddCadMetadataEvidence(document, evidence, candidates);
        AddTextEvidence(document, sheetRegions, evidence, candidates);
        AddScaleBarEvidence(document, evidence, candidates);
        var scaleGroups = BuildScaleGroups(document, sheetRegions, evidence);

        var selectedScaleGroup = SelectDefaultScaleGroup(scaleGroups);
        if (selectedScaleGroup is not null)
        {
            return new PlanCalibration(
                selectedScaleGroup.DrawingUnit,
                PlanMeasurementUnit.Millimeter,
                selectedScaleGroup.ScaleRatio,
                selectedScaleGroup.MillimetersPerDrawingUnit,
                selectedScaleGroup.Confidence,
                evidence.ToArray(),
                scaleGroups);
        }

        var unitHint = evidence
            .Where(item => item.Unit is not PlanMeasurementUnit.Unknown)
            .OrderByDescending(item => item.Confidence.Value)
            .FirstOrDefault();

        if (unitHint is not null)
        {
            return new PlanCalibration(
                PlanMeasurementUnit.Unknown,
                unitHint.Unit,
                null,
                null,
                new Confidence(Math.Min(0.45, unitHint.Confidence.Value)),
                evidence.ToArray(),
                scaleGroups);
        }

        return evidence.Count == 0
            ? PlanCalibration.Empty
            : new PlanCalibration(
                PlanMeasurementUnit.Unknown,
                PlanMeasurementUnit.Unknown,
                null,
                null,
                Confidence.Low,
                evidence.ToArray(),
                scaleGroups);
    }

    private static CalibrationScaleGroup? SelectDefaultScaleGroup(IReadOnlyList<CalibrationScaleGroup> scaleGroups) =>
        scaleGroups
            .Where(group => group.MillimetersPerDrawingUnit is > 0)
            .Where(group => IsDefaultCalibrationScope(group.Scope))
            .OrderByDescending(group => group.Confidence.Value)
            .ThenBy(group => DefaultCalibrationScopeRank(group.Scope))
            .ThenByDescending(group => group.EvidenceCount)
            .FirstOrDefault();

    private static bool IsDefaultCalibrationScope(CalibrationScaleScope scope) =>
        scope is CalibrationScaleScope.Document
            or CalibrationScaleScope.MainFloorPlan
            or CalibrationScaleScope.Dimensions
            or CalibrationScaleScope.TitleBlock
            or CalibrationScaleScope.Page;

    private static int DefaultCalibrationScopeRank(CalibrationScaleScope scope) =>
        scope switch
        {
            CalibrationScaleScope.Document => 0,
            CalibrationScaleScope.MainFloorPlan => 1,
            CalibrationScaleScope.Dimensions => 2,
            CalibrationScaleScope.TitleBlock => 3,
            CalibrationScaleScope.Page => 4,
            _ => 10
        };

    private static void AddCadMetadataEvidence(
        PlanDocument document,
        List<CalibrationEvidence> evidence,
        List<CalibrationCandidate> candidates)
    {
        if (!TryReadProperty(document.Metadata.Properties, "dxf.defaultDrawingUnits", out var units)
            && !TryReadProperty(document.Metadata.Properties, "defaultDrawingUnits", out units))
        {
            return;
        }

        var unit = MapCadUnit(units);
        var mmPerDrawingUnit = MillimetersPerUnit(unit);
        var confidence = unit == PlanMeasurementUnit.Unitless || unit == PlanMeasurementUnit.Unknown
            ? new Confidence(0.25)
            : new Confidence(0.82);

        var item = new CalibrationEvidence(
            CalibrationEvidenceKind.CadMetadata,
            null,
            null,
            units,
            unit,
            null,
            mmPerDrawingUnit,
            confidence,
            unit == PlanMeasurementUnit.Unitless || unit == PlanMeasurementUnit.Unknown
                ? "DXF $INSUNITS was unitless or unsupported; measurements need more evidence."
                : $"DXF $INSUNITS declares one drawing unit as {units}.");

        evidence.Add(item);

        if (mmPerDrawingUnit is > 0)
        {
            candidates.Add(
                new CalibrationCandidate(
                    unit,
                    null,
                    mmPerDrawingUnit.Value,
                    confidence));
        }
    }

    private static void AddTextEvidence(
        PlanDocument document,
        IReadOnlyList<SheetRegion> sheetRegions,
        List<CalibrationEvidence> evidence,
        List<CalibrationCandidate> candidates)
    {
        foreach (var page in document.Pages)
        {
            var lines = CalibrationLineSpatialIndex.Create(EnumerateLines(page).ToArray());
            for (var index = 0; index < page.Primitives.Count; index++)
            {
                if (page.Primitives[index] is not TextPrimitive text)
                {
                    continue;
                }

                var sourceId = PrimitiveId(page.Number, index, text);
                var rawText = text.Text.Trim();
                if (rawText.Length == 0)
                {
                    continue;
                }

                if (LooksLikeNoScale(rawText))
                {
                    evidence.Add(
                        new CalibrationEvidence(
                            CalibrationEvidenceKind.NoScaleText,
                            page.Number,
                            sourceId,
                            rawText,
                            PlanMeasurementUnit.Unknown,
                            null,
                            null,
                            Confidence.Medium,
                            "Text indicates this view is not to scale."));
                    continue;
                }

                if (TryParseScaleRatio(rawText, out var scaleRatio, out var scaleDescription))
                {
                    var inTitleBlock = IsInsideRegion(text.Bounds.Center, sheetRegions, page.Number, RegionKind.TitleBlock);
                    var scaleConfidence = new Confidence(inTitleBlock ? 0.9 : 0.72);
                    double? mmPerDrawingUnit = IsPdfLike(document, text)
                        ? scaleRatio * MillimetersPerInch / PdfPointsPerInch
                        : null;

                    evidence.Add(
                        new CalibrationEvidence(
                            CalibrationEvidenceKind.ScaleText,
                            page.Number,
                            sourceId,
                            rawText,
                            PlanMeasurementUnit.Millimeter,
                            scaleRatio,
                            mmPerDrawingUnit,
                            scaleConfidence,
                            scaleDescription));

                    if (mmPerDrawingUnit is > 0)
                    {
                        candidates.Add(
                            new CalibrationCandidate(
                                PlanMeasurementUnit.PdfPoint,
                                scaleRatio,
                                mmPerDrawingUnit.Value,
                                scaleConfidence));
                    }
                }

                if (!TryParseDimensionText(rawText, out var dimension))
                {
                    continue;
                }

                var dimensionContext = IsDimensionContext(text, sheetRegions, page.Number);
                var match = dimensionContext
                    ? FindNearbyDimensionLine(text, lines, sheetRegions, page)
                    : null;

                double? mmPerUnit = match is null ? null : dimension.Millimeters / match.Segment.Length;
                var dimensionConfidence = new Confidence(mmPerUnit is > 0 ? 0.68 : 0.42);
                evidence.Add(
                    new CalibrationEvidence(
                        match is null ? CalibrationEvidenceKind.UnitHint : CalibrationEvidenceKind.DimensionText,
                        page.Number,
                        sourceId,
                        rawText,
                        dimension.Unit,
                        null,
                        mmPerUnit,
                        dimensionConfidence,
                        match is null
                            ? $"Dimension text implies {dimension.Unit}, but no nearby dimension line was matched."
                            : $"Dimension text was matched to a nearby {Math.Round(match.Segment.Length, 3).ToString(CultureInfo.InvariantCulture)} drawing-unit line."));

                if (mmPerUnit is > 0)
                {
                    candidates.Add(
                        new CalibrationCandidate(
                            PlanMeasurementUnit.DrawingUnit,
                            null,
                            mmPerUnit.Value,
                            dimensionConfidence));
                }
            }
        }
    }

    private static void AddScaleBarEvidence(
        PlanDocument document,
        List<CalibrationEvidence> evidence,
        List<CalibrationCandidate> candidates)
    {
        foreach (var page in document.Pages)
        {
            var lines = EnumerateLines(page)
                .Where(line => IsScaleBarLineCandidate(line.Segment, page))
                .ToArray();
            var texts = CalibrationTextSpatialIndex.Create(EnumerateText(page).ToArray());
            var labelDistance = Math.Max(18, Math.Min(page.Size.Width, page.Size.Height) * 0.055);

            foreach (var line in lines)
            {
                if (!TryMatchScaleBar(line, texts, labelDistance, out var match))
                {
                    continue;
                }

                var mmPerUnit = match.Millimeters / line.Segment.Length;
                if (mmPerUnit <= 0)
                {
                    continue;
                }

                var confidence = new Confidence(LooksLikeScaleLayer(line.Primitive) ? 0.82 : 0.76);
                var drawingUnit = IsPdfLike(document, line.Primitive)
                    ? PlanMeasurementUnit.PdfPoint
                    : PlanMeasurementUnit.DrawingUnit;
                var description =
                    $"Scale bar maps {Math.Round(line.Segment.Length, 3).ToString(CultureInfo.InvariantCulture)} drawing units to {match.Text}.";

                evidence.Add(
                    new CalibrationEvidence(
                        CalibrationEvidenceKind.ScaleBar,
                        page.Number,
                        line.PrimitiveId,
                        match.Text,
                        match.Unit,
                        null,
                        mmPerUnit,
                        confidence,
                        description));

                candidates.Add(
                    new CalibrationCandidate(
                        drawingUnit,
                        null,
                        mmPerUnit,
                        confidence));
            }
        }
    }

    private static IReadOnlyList<CalibrationScaleGroup> BuildScaleGroups(
        PlanDocument document,
        IReadOnlyList<SheetRegion> sheetRegions,
        IReadOnlyList<CalibrationEvidence> evidence)
    {
        var primitiveIndex = BuildPrimitiveIndex(document);
        var builders = new List<CalibrationScaleGroupBuilder>();

        foreach (var item in evidence.Where(IsScaleGroupEvidence))
        {
            var primitiveInfo = item.SourcePrimitiveId is not null
                && primitiveIndex.TryGetValue(item.SourcePrimitiveId, out var found)
                    ? found
                    : null;
            var sourceRegion = ResolveScaleRegion(item, primitiveInfo, sheetRegions);
            var scope = ResolveScaleScope(item, primitiveInfo, sourceRegion);
            var drawingUnit = ResolveDrawingUnit(document, item, primitiveInfo);
            var builder = builders.FirstOrDefault(candidate =>
                candidate.PageNumber == item.PageNumber
                && candidate.Scope == scope
                && candidate.DrawingUnit == drawingUnit
                && IsSimilarScale(candidate, item));

            if (builder is null)
            {
                builder = new CalibrationScaleGroupBuilder(item.PageNumber, scope, drawingUnit, item.Unit);
                builders.Add(builder);
            }

            builder.Add(item, primitiveInfo, sourceRegion);
        }

        return builders
            .OrderBy(builder => builder.PageNumber ?? int.MaxValue)
            .ThenBy(builder => builder.Scope)
            .ThenBy(builder => builder.MillimetersPerDrawingUnit ?? double.MaxValue)
            .ThenBy(builder => builder.ScaleRatio ?? double.MaxValue)
            .Select((builder, index) => builder.ToGroup(index + 1))
            .ToArray();
    }

    private static Dictionary<string, PrimitiveInfo> BuildPrimitiveIndex(PlanDocument document)
    {
        var result = new Dictionary<string, PrimitiveInfo>(StringComparer.Ordinal);
        foreach (var page in document.Pages)
        {
            for (var index = 0; index < page.Primitives.Count; index++)
            {
                var primitive = page.Primitives[index];
                result[PrimitiveId(page.Number, index, primitive)] = new PrimitiveInfo(page.Number, primitive.Bounds, primitive);
            }
        }

        return result;
    }

    private static bool IsScaleGroupEvidence(CalibrationEvidence evidence) =>
        evidence.Kind is CalibrationEvidenceKind.CadMetadata
            or CalibrationEvidenceKind.ScaleText
            or CalibrationEvidenceKind.ScaleBar
            or CalibrationEvidenceKind.DimensionText
        && (evidence.MillimetersPerDrawingUnit is > 0 || evidence.ScaleRatio is > 0 || evidence.Unit is not PlanMeasurementUnit.Unknown);

    private static CalibrationScaleScope ResolveScaleScope(
        CalibrationEvidence evidence,
        PrimitiveInfo? primitiveInfo,
        SheetRegion? sourceRegion)
    {
        if (evidence.Kind == CalibrationEvidenceKind.CadMetadata)
        {
            return CalibrationScaleScope.Document;
        }

        if (primitiveInfo is null)
        {
            return evidence.PageNumber is null ? CalibrationScaleScope.Document : CalibrationScaleScope.Unknown;
        }

        return sourceRegion?.Kind switch
        {
            RegionKind.TitleBlock => CalibrationScaleScope.TitleBlock,
            RegionKind.MainFloorPlan => CalibrationScaleScope.MainFloorPlan,
            RegionKind.Dimensions => CalibrationScaleScope.Dimensions,
            RegionKind.Notes => CalibrationScaleScope.Notes,
            RegionKind.KeyPlan => CalibrationScaleScope.KeyPlan,
            RegionKind.Sheet => CalibrationScaleScope.Page,
            null => CalibrationScaleScope.Page,
            _ => CalibrationScaleScope.OtherRegion
        };
    }

    private static SheetRegion? ResolveScaleRegion(
        CalibrationEvidence evidence,
        PrimitiveInfo? primitiveInfo,
        IReadOnlyList<SheetRegion> sheetRegions)
    {
        if (evidence.Kind == CalibrationEvidenceKind.CadMetadata || primitiveInfo is null)
        {
            return null;
        }

        return sheetRegions
            .Where(region => region.PageNumber == primitiveInfo.PageNumber)
            .Where(region => region.Bounds.Contains(primitiveInfo.Bounds.Center, 6))
            .OrderBy(region => CalibrationRegionPriority(region.Kind))
            .ThenBy(region => region.Bounds.Width * region.Bounds.Height)
            .FirstOrDefault();
    }

    private static int CalibrationRegionPriority(RegionKind kind) =>
        kind switch
        {
            RegionKind.TitleBlock => 0,
            RegionKind.Dimensions => 1,
            RegionKind.KeyPlan => 2,
            RegionKind.Notes => 3,
            RegionKind.Legend => 4,
            RegionKind.MainFloorPlan => 5,
            RegionKind.Sheet => 6,
            _ => 7
        };

    private static PlanMeasurementUnit ResolveDrawingUnit(
        PlanDocument document,
        CalibrationEvidence evidence,
        PrimitiveInfo? primitiveInfo)
    {
        if (evidence.Kind == CalibrationEvidenceKind.CadMetadata && evidence.Unit is not PlanMeasurementUnit.Unknown)
        {
            return evidence.Unit;
        }

        return primitiveInfo is not null && IsPdfLike(document, primitiveInfo.Primitive)
            ? PlanMeasurementUnit.PdfPoint
            : PlanMeasurementUnit.DrawingUnit;
    }

    private static bool IsSimilarScale(CalibrationScaleGroupBuilder group, CalibrationEvidence evidence)
    {
        if (group.MillimetersPerDrawingUnit is > 0 && evidence.MillimetersPerDrawingUnit is > 0)
        {
            return RelativeDifference(group.MillimetersPerDrawingUnit.Value, evidence.MillimetersPerDrawingUnit.Value) <= 0.03;
        }

        if (group.ScaleRatio is > 0 && evidence.ScaleRatio is > 0)
        {
            return RelativeDifference(group.ScaleRatio.Value, evidence.ScaleRatio.Value) <= 0.03;
        }

        return false;
    }

    private static double RelativeDifference(double first, double second) =>
        Math.Abs(first - second) / Math.Max(1, Math.Max(Math.Abs(first), Math.Abs(second)));

    private static bool TryReadProperty(
        IReadOnlyDictionary<string, string> properties,
        string key,
        out string value)
    {
        foreach (var property in properties)
        {
            if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryParseScaleRatio(string text, out double ratio, out string description)
    {
        var imperial = ImperialScaleRegex.Match(text);
        if (imperial.Success)
        {
            var paperInches = ParseNumber(imperial.Groups["paper"].Value);
            var realInches = (ParseNumber(imperial.Groups["feet"].Value) * 12.0)
                + (imperial.Groups["inches"].Success ? ParseNumber(imperial.Groups["inches"].Value) : 0);

            if (paperInches > 0 && realInches > 0)
            {
                ratio = realInches / paperInches;
                description = $"Architectural scale text maps paper inches to real inches at 1:{Math.Round(ratio, 3).ToString(CultureInfo.InvariantCulture)}.";
                return true;
            }
        }

        var scale = RatioScaleRegex.Match(text);
        if (scale.Success && LooksLikeScaleText(text))
        {
            ratio = ParseNumber(scale.Groups["ratio"].Value);
            description = $"Scale text declares a 1:{Math.Round(ratio, 3).ToString(CultureInfo.InvariantCulture)} ratio.";
            return ratio > 0;
        }

        ratio = 0;
        description = string.Empty;
        return false;
    }

    private static bool TryParseDimensionText(string text, out ParsedDimension dimension)
    {
        if (LooksLikeScaleText(text) || LooksLikeAreaMeasurementText(text))
        {
            dimension = default;
            return false;
        }

        var wholeMillimeters = MetricWholeMillimeterDimensionRegex.Match(text);
        if (wholeMillimeters.Success)
        {
            var value = ParseNumber(wholeMillimeters.Groups["value"].Value.Replace(" ", string.Empty));
            dimension = new ParsedDimension(value, PlanMeasurementUnit.Millimeter);
            return dimension.Millimeters > 0;
        }

        var metric = MetricDimensionRegex.Match(text);
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

            dimension = new ParsedDimension(value * (MillimetersPerUnit(unit) ?? 0), unit);
            return dimension.Millimeters > 0;
        }

        var feet = FeetDimensionRegex.Match(text);
        if (feet.Success)
        {
            var realInches = (ParseNumber(feet.Groups["feet"].Value) * 12.0)
                + (feet.Groups["inches"].Success ? ParseNumber(feet.Groups["inches"].Value) : 0);
            dimension = new ParsedDimension(realInches * MillimetersPerInch, PlanMeasurementUnit.Foot);
            return dimension.Millimeters > 0;
        }

        var inches = InchesDimensionRegex.Match(text);
        if (inches.Success)
        {
            dimension = new ParsedDimension(ParseNumber(inches.Groups["inches"].Value) * MillimetersPerInch, PlanMeasurementUnit.Inch);
            return dimension.Millimeters > 0;
        }

        dimension = default;
        return false;
    }

    private static PrimitiveLineCandidate? FindNearbyDimensionLine(
        TextPrimitive text,
        CalibrationLineSpatialIndex lines,
        IReadOnlyList<SheetRegion> sheetRegions,
        PlanPage page)
    {
        var dimensionRegion = sheetRegions.FirstOrDefault(region =>
            region.PageNumber == page.Number
            && region.Kind == RegionKind.Dimensions
            && region.Bounds.Contains(text.Bounds.Center, 8));
        var searchDistance = Math.Max(40, Math.Min(page.Size.Width, page.Size.Height) * 0.12);
        var searchBounds = dimensionRegion is null
            ? BoundsAround(text.Bounds.Center, searchDistance)
            : dimensionRegion.Bounds.Inflate(8);

        return lines
            .Query(searchBounds, includeDimensionLayerLines: true)
            .Where(line => line.Segment.Length > 8)
            .Where(line => line.Segment.IsHorizontal(2) || line.Segment.IsVertical(2))
            .Where(line => dimensionRegion is null || dimensionRegion.Bounds.Intersects(line.Segment.Bounds.Inflate(8)))
            .Select(line => new
            {
                Line = line,
                Distance = line.Segment.DistanceToPoint(text.Bounds.Center),
                LayerBonus = LooksLikeDimensionLayer(line.Primitive) ? 24 : 0
            })
            .Where(candidate => candidate.Distance <= searchDistance || candidate.LayerBonus > 0)
            .OrderBy(candidate => candidate.Distance - candidate.LayerBonus)
            .Select(candidate => candidate.Line)
            .FirstOrDefault();
    }

    private static IEnumerable<PrimitiveLineCandidate> EnumerateLines(PlanPage page)
    {
        for (var index = 0; index < page.Primitives.Count; index++)
        {
            var primitive = page.Primitives[index];
            var primitiveId = PrimitiveId(page.Number, index, primitive);

            switch (primitive)
            {
                case LinePrimitive line:
                    yield return new PrimitiveLineCandidate(line.Segment, primitiveId, primitive);
                    break;

                case RectanglePrimitive rectangle:
                    foreach (var edge in RectangleToLines(rectangle.Rectangle))
                    {
                        yield return new PrimitiveLineCandidate(edge, primitiveId, primitive);
                    }

                    break;

                case PolylinePrimitive polyline:
                    foreach (var segment in PolylineToLines(polyline))
                    {
                        yield return new PrimitiveLineCandidate(segment, primitiveId, primitive);
                    }

                    break;
            }
        }
    }

    private static IEnumerable<TextCandidate> EnumerateText(PlanPage page)
    {
        for (var index = 0; index < page.Primitives.Count; index++)
        {
            if (page.Primitives[index] is TextPrimitive text)
            {
                yield return new TextCandidate(
                    text,
                    PrimitiveId(page.Number, index, text),
                    text.Text.Trim(),
                    text.Bounds);
            }
        }
    }

    private static bool IsScaleBarLineCandidate(PlanLineSegment line, PlanPage page)
    {
        var minLength = Math.Max(20, Math.Min(page.Size.Width, page.Size.Height) * 0.04);
        var maxLength = Math.Max(page.Size.Width, page.Size.Height) * 0.75;
        return line.Length >= minLength
            && line.Length <= maxLength
            && (line.IsHorizontal(2) || line.IsVertical(2));
    }

    private static bool TryMatchScaleBar(
        PrimitiveLineCandidate line,
        CalibrationTextSpatialIndex texts,
        double labelDistance,
        out ScaleBarMatch match)
    {
        var startZero = FindZeroLabel(line.Segment.Start, texts, labelDistance);
        var endZero = FindZeroLabel(line.Segment.End, texts, labelDistance);
        var startDimension = FindDimensionLabel(line.Segment.Start, texts, labelDistance);
        var endDimension = FindDimensionLabel(line.Segment.End, texts, labelDistance);

        if (startZero is not null && endDimension is not null)
        {
            match = endDimension.Value;
            return true;
        }

        if (endZero is not null && startDimension is not null)
        {
            match = startDimension.Value;
            return true;
        }

        match = default;
        return false;
    }

    private static TextCandidate? FindZeroLabel(
        PlanPoint point,
        CalibrationTextSpatialIndex texts,
        double maxDistance) =>
        texts
            .QueryAround(point, maxDistance)
            .Where(text => IsZeroLabel(text.Text))
            .Select(text => new
            {
                Text = text,
                Distance = text.Bounds.Center.DistanceTo(point)
            })
            .Where(candidate => candidate.Distance <= maxDistance)
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => candidate.Text)
            .FirstOrDefault();

    private static ScaleBarMatch? FindDimensionLabel(
        PlanPoint point,
        CalibrationTextSpatialIndex texts,
        double maxDistance)
    {
        var candidate = texts
            .QueryAround(point, maxDistance)
            .Select(text => new
            {
                Text = text,
                Distance = text.Bounds.Center.DistanceTo(point),
                Parsed = TryParseDimensionText(text.Text, out var parsed) ? parsed : (ParsedDimension?)null
            })
            .Where(candidate => candidate.Parsed is not null && candidate.Distance <= maxDistance)
            .OrderBy(candidate => candidate.Distance)
            .FirstOrDefault();

        return candidate is null
            ? null
            : new ScaleBarMatch(
                candidate.Text.Text,
                candidate.Parsed!.Value.Millimeters,
                candidate.Parsed.Value.Unit,
                candidate.Text.SourceId);
    }

    private static bool IsZeroLabel(string text)
    {
        var normalized = text.Trim().TrimEnd('.');
        return normalized is "0" or "0.0" or "0,0"
            || normalized.Equals("0 m", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("0 mm", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("0 cm", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("0 ft", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("0 in", StringComparison.OrdinalIgnoreCase);
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

    private static bool IsDimensionContext(
        TextPrimitive text,
        IReadOnlyList<SheetRegion> sheetRegions,
        int pageNumber) =>
        LooksLikeDimensionLayer(text)
        || IsInsideRegion(text.Bounds.Center, sheetRegions, pageNumber, RegionKind.Dimensions);

    private static bool IsInsideRegion(
        PlanPoint point,
        IReadOnlyList<SheetRegion> sheetRegions,
        int pageNumber,
        RegionKind kind) =>
        sheetRegions.Any(region =>
            region.PageNumber == pageNumber
            && region.Kind == kind
            && region.Bounds.Contains(point, 6));

    private static bool LooksLikeScaleText(string text) =>
        text.Contains("scale", StringComparison.OrdinalIgnoreCase)
        || text.Contains("scl", StringComparison.OrdinalIgnoreCase)
        || RatioScaleRegex.IsMatch(text)
        || ImperialScaleRegex.IsMatch(text);

    private static bool LooksLikeAreaMeasurementText(string text) =>
        AreaMeasurementRegex.IsMatch(text);

    private static bool LooksLikeNoScale(string text) =>
        text.Contains("NTS", StringComparison.OrdinalIgnoreCase)
        || text.Contains("NOT TO SCALE", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeDimensionLayer(PlanPrimitive primitive)
    {
        var layer = primitive.Source.Layer ?? primitive.Layer;
        return !string.IsNullOrWhiteSpace(layer)
            && (layer.Contains("dim", StringComparison.OrdinalIgnoreCase)
                || layer.Contains("anno", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeScaleLayer(PlanPrimitive primitive)
    {
        var text = string.Join(" ", primitive.Layer, primitive.Source.Layer, primitive.Source.EntityType, primitive.Source.BlockName);
        return text.Contains("scale", StringComparison.OrdinalIgnoreCase)
            || text.Contains("scalebar", StringComparison.OrdinalIgnoreCase)
            || text.Contains("bar-scale", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPdfLike(PlanDocument document, PlanPrimitive primitive) =>
        string.Equals(primitive.Source.SourceFormat, "pdf", StringComparison.OrdinalIgnoreCase)
        || (document.Metadata.Properties.TryGetValue("format", out var format)
            && string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase));

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

    private static PlanMeasurementUnit MapCadUnit(string value) =>
        value.Trim() switch
        {
            "Unitless" => PlanMeasurementUnit.Unitless,
            "Millimeters" => PlanMeasurementUnit.Millimeter,
            "Centimeters" => PlanMeasurementUnit.Centimeter,
            "Meters" => PlanMeasurementUnit.Meter,
            "Inches" => PlanMeasurementUnit.Inch,
            "Feet" or "USSurveyFeet" => PlanMeasurementUnit.Foot,
            _ => PlanMeasurementUnit.Unknown
        };

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

    private static string PrimitiveId(int pageNumber, int primitiveIndex, PlanPrimitive primitive) =>
        primitive.SourceId ?? primitive.Source.SourceId ?? $"p{pageNumber}:primitive:{primitiveIndex}";

    private sealed record CalibrationCandidate(
        PlanMeasurementUnit DrawingUnit,
        double? ScaleRatio,
        double MillimetersPerDrawingUnit,
        Confidence Confidence);

    private sealed record PrimitiveInfo(int PageNumber, PlanRect Bounds, PlanPrimitive Primitive);

    private sealed class CalibrationScaleGroupBuilder
    {
        private readonly List<CalibrationEvidence> evidence = new();
        private readonly List<PlanRect> evidenceBounds = new();
        private readonly List<string> sourceRegionIds = new();

        public CalibrationScaleGroupBuilder(
            int? pageNumber,
            CalibrationScaleScope scope,
            PlanMeasurementUnit drawingUnit,
            PlanMeasurementUnit evidenceUnit)
        {
            PageNumber = pageNumber;
            Scope = scope;
            DrawingUnit = drawingUnit;
            EvidenceUnit = evidenceUnit;
        }

        public int? PageNumber { get; }

        public CalibrationScaleScope Scope { get; }

        public PlanMeasurementUnit DrawingUnit { get; }

        public PlanMeasurementUnit EvidenceUnit { get; }

        public double? ScaleRatio => Average(evidence.Select(item => item.ScaleRatio));

        public double? MillimetersPerDrawingUnit => Average(evidence.Select(item => item.MillimetersPerDrawingUnit));

        public void Add(
            CalibrationEvidence item,
            PrimitiveInfo? primitiveInfo,
            SheetRegion? sourceRegion)
        {
            evidence.Add(item);
            if (primitiveInfo is not null && !primitiveInfo.Bounds.IsEmpty)
            {
                evidenceBounds.Add(primitiveInfo.Bounds);
            }

            if (sourceRegion is not null
                && !sourceRegionIds.Contains(sourceRegion.Id, StringComparer.Ordinal))
            {
                sourceRegionIds.Add(sourceRegion.Id);
            }
        }

        public CalibrationScaleGroup ToGroup(int ordinal)
        {
            var sourcePrimitiveIds = evidence
                .Select(item => item.SourcePrimitiveId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var groupEvidence = evidence
                .Select(item => item.Description)
                .Where(description => !string.IsNullOrWhiteSpace(description))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var idPrefix = PageNumber is null
                ? "document"
                : $"page:{PageNumber.Value}";

            return new CalibrationScaleGroup(
                $"{idPrefix}:scale-group:{ordinal}",
                PageNumber,
                Scope,
                DrawingUnit,
                EvidenceUnit,
                ScaleRatio,
                MillimetersPerDrawingUnit,
                evidence.Count,
                new Confidence(Math.Min(0.95, evidence.Average(item => item.Confidence.Value))),
                sourcePrimitiveIds,
                sourceRegionIds.ToArray(),
                evidenceBounds.Count == 0 ? null : PlanRect.Union(evidenceBounds),
                groupEvidence);
        }

        private static double? Average(IEnumerable<double?> values)
        {
            var measured = values
                .Where(value => value is > 0)
                .Select(value => value!.Value)
                .ToArray();
            return measured.Length == 0 ? null : measured.Average();
        }
    }

    private readonly record struct ParsedDimension(double Millimeters, PlanMeasurementUnit Unit);

    private readonly record struct ScaleBarMatch(
        string Text,
        double Millimeters,
        PlanMeasurementUnit Unit,
        string SourceId);

    private sealed record TextCandidate(
        TextPrimitive Primitive,
        string SourceId,
        string Text,
        PlanRect Bounds);

    private sealed class CalibrationTextSpatialIndex
    {
        private readonly Dictionary<Cell, List<TextCandidate>> cells;
        private readonly double cellSize;

        private CalibrationTextSpatialIndex(Dictionary<Cell, List<TextCandidate>> cells, double cellSize)
        {
            this.cells = cells;
            this.cellSize = cellSize;
        }

        public static CalibrationTextSpatialIndex Create(IReadOnlyList<TextCandidate> texts)
        {
            var cellSize = 48.0;
            var cells = new Dictionary<Cell, List<TextCandidate>>();
            foreach (var text in texts)
            {
                AddToCells(cells, cellSize, text.Bounds, text);
            }

            return new CalibrationTextSpatialIndex(cells, cellSize);
        }

        public IEnumerable<TextCandidate> QueryAround(PlanPoint point, double radius) =>
            Query(BoundsAround(point, radius));

        private IEnumerable<TextCandidate> Query(PlanRect bounds)
        {
            if (bounds.IsEmpty || cells.Count == 0)
            {
                yield break;
            }

            var yielded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cell in CellsFor(bounds, cellSize))
            {
                if (!cells.TryGetValue(cell, out var bucket))
                {
                    continue;
                }

                foreach (var text in bucket)
                {
                    if (yielded.Add(text.SourceId) && text.Bounds.Intersects(bounds))
                    {
                        yield return text;
                    }
                }
            }
        }
    }

    private sealed class CalibrationLineSpatialIndex
    {
        private readonly Dictionary<Cell, List<PrimitiveLineCandidate>> cells;
        private readonly IReadOnlyList<PrimitiveLineCandidate> dimensionLayerLines;
        private readonly double cellSize;

        private CalibrationLineSpatialIndex(
            Dictionary<Cell, List<PrimitiveLineCandidate>> cells,
            IReadOnlyList<PrimitiveLineCandidate> dimensionLayerLines,
            double cellSize)
        {
            this.cells = cells;
            this.dimensionLayerLines = dimensionLayerLines;
            this.cellSize = cellSize;
        }

        public static CalibrationLineSpatialIndex Create(IReadOnlyList<PrimitiveLineCandidate> lines)
        {
            var cellSize = 96.0;
            var cells = new Dictionary<Cell, List<PrimitiveLineCandidate>>();
            var dimensionLayerLines = new List<PrimitiveLineCandidate>();

            foreach (var line in lines)
            {
                AddToCells(cells, cellSize, line.Segment.Bounds, line);
                if (LooksLikeDimensionLayer(line.Primitive))
                {
                    dimensionLayerLines.Add(line);
                }
            }

            return new CalibrationLineSpatialIndex(cells, dimensionLayerLines, cellSize);
        }

        public IEnumerable<PrimitiveLineCandidate> Query(
            PlanRect bounds,
            bool includeDimensionLayerLines)
        {
            if (bounds.IsEmpty)
            {
                yield break;
            }

            var yielded = new HashSet<PrimitiveLineCandidate>();
            foreach (var cell in CellsFor(bounds, cellSize))
            {
                if (!cells.TryGetValue(cell, out var bucket))
                {
                    continue;
                }

                foreach (var line in bucket)
                {
                    if (yielded.Add(line) && line.Segment.Bounds.Intersects(bounds))
                    {
                        yield return line;
                    }
                }
            }

            if (!includeDimensionLayerLines)
            {
                yield break;
            }

            foreach (var line in dimensionLayerLines)
            {
                if (yielded.Add(line))
                {
                    yield return line;
                }
            }
        }
    }

    private static PlanRect BoundsAround(PlanPoint point, double radius) =>
        PlanRect.FromEdges(
            point.X - radius,
            point.Y - radius,
            point.X + radius,
            point.Y + radius);

    private static void AddToCells<T>(
        IDictionary<Cell, List<T>> cells,
        double cellSize,
        PlanRect bounds,
        T item)
    {
        foreach (var cell in CellsFor(bounds, cellSize))
        {
            if (!cells.TryGetValue(cell, out var bucket))
            {
                bucket = new List<T>();
                cells[cell] = bucket;
            }

            bucket.Add(item);
        }
    }

    private static IEnumerable<Cell> CellsFor(PlanRect bounds, double cellSize)
    {
        if (bounds.IsEmpty)
        {
            yield break;
        }

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

    private sealed record PrimitiveLineCandidate(
        PlanLineSegment Segment,
        string PrimitiveId,
        PlanPrimitive Primitive);
}
