using System.Globalization;

namespace OpenPlanTrace;

public static class ScanReviewQueueKinds
{
    public const string MeasurementOutlier = "MeasurementOutlier";
    public const string WallEvidenceReview = "WallEvidenceReview";
    public const string SurfacePatternReview = "SurfacePatternReview";
    public const string SurfacePatternWallOverlapReview = "SurfacePatternWallOverlapReview";
    public const string SuppressedWallPatternReview = "SuppressedWallPatternReview";
    public const string WallGraphGapReview = "WallGraphGapReview";
    public const string ObjectGroupReview = "ObjectGroupReview";
    public const string ObjectAggregateReview = "ObjectAggregateReview";
    public const string OpeningReview = "OpeningReview";
}

public sealed record ScanReviewQueueSummary(
    int Count,
    IReadOnlyDictionary<string, int> KindCounts,
    IReadOnlyDictionary<string, int> SeverityCounts)
{
    public const int WallEvidenceReviewQueueLimit = 32;

    public const int WallGraphGapReviewQueueLimit = 24;

    public const int SurfacePatternWallOverlapReviewQueueLimit = 12;

    public const int ObjectGroupReviewQueueLimit = 20;

    public static ScanReviewQueueSummary Empty { get; } =
        new(
            0,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

    public static ScanReviewQueueSummary From(PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var builder = new ScanReviewQueueSummaryBuilder();
        var measurementSeverity = result.MeasurementConsistency.HasBlockingOutliers
            ? DiagnosticSeverity.Warning
            : DiagnosticSeverity.Info;

        foreach (var check in result.MeasurementConsistency.Checks.Where(check => check.Status == MeasurementConsistencyStatus.Outlier))
        {
            builder.Add(ScanReviewQueueKinds.MeasurementOutlier, measurementSeverity);
        }

        foreach (var diagnostic in result.Diagnostics.Messages.Where(diagnostic => IsSuppressedWallPatternDiagnostic(diagnostic.Code)))
        {
            builder.Add(ScanReviewQueueKinds.SuppressedWallPatternReview, diagnostic.Severity);
        }

        foreach (var assessment in QueuedWallEvidenceReviews(result.WallEvidenceMap))
        {
            builder.Add(ScanReviewQueueKinds.WallEvidenceReview, WallEvidenceReviewSeverity(assessment));
        }

        foreach (var pattern in result.SurfacePatterns.Where(pattern => pattern.RequiresReview))
        {
            builder.Add(ScanReviewQueueKinds.SurfacePatternReview, DiagnosticSeverity.Info);
        }

        foreach (var diagnostic in QueuedSurfacePatternWallOverlapDiagnostics(result.Diagnostics.Messages))
        {
            builder.Add(ScanReviewQueueKinds.SurfacePatternWallOverlapReview, diagnostic.Severity);
        }

        foreach (var diagnostic in QueuedWallGraphGapDiagnostics(result.Diagnostics.Messages))
        {
            builder.Add(ScanReviewQueueKinds.WallGraphGapReview, diagnostic.Severity);
        }

        foreach (var group in QueuedObjectGroups(result.ObjectGroups))
        {
            builder.Add(ScanReviewQueueKinds.ObjectGroupReview, DiagnosticSeverity.Info);
        }

        foreach (var aggregate in result.ObjectAggregates.Where(aggregate => aggregate.RequiresReview))
        {
            builder.Add(ScanReviewQueueKinds.ObjectAggregateReview, DiagnosticSeverity.Info);
        }

        foreach (var opening in result.Openings.Where(NeedsOpeningReview))
        {
            builder.Add(
                ScanReviewQueueKinds.OpeningReview,
                opening.Placement is null ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info);
        }

        return builder.ToSummary();
    }

    public static bool NeedsOpeningReview(OpeningCandidate opening) =>
        OpeningReviewReasons(opening).Count > 0;

    public static bool OpeningPlacementIsCoordinateReady(OpeningCandidate opening) =>
        opening.Placement is not null
        && IsOpeningPlacementSpanCoherent(opening.Placement);

    public static IReadOnlyList<string> OpeningReviewReasons(OpeningCandidate opening)
    {
        ArgumentNullException.ThrowIfNull(opening);

        var reasons = new List<string>();
        if (opening.Placement is null)
        {
            reasons.Add("opening is not anchored to a host-wall placement reference");
        }
        else if (!IsOpeningPlacementSpanCoherent(opening.Placement))
        {
            reasons.Add("opening placement offsets or host-wall parameters are inconsistent");
        }

        if (opening.Operation == OpeningOperation.Unknown)
        {
            reasons.Add("opening operation is unknown");
        }

        if (opening.Confidence.Value < 0.5)
        {
            reasons.Add("opening confidence is below 0.5");
        }

        return reasons;
    }

    private static bool IsOpeningPlacementSpanCoherent(OpeningPlacement placement)
    {
        if (placement.ReferenceLine.Length <= 0.001
            || placement.LengthDrawingUnits <= 0.001
            || !IsFinite(placement.StartOffsetDrawingUnits)
            || !IsFinite(placement.EndOffsetDrawingUnits)
            || !IsFinite(placement.CenterOffsetDrawingUnits)
            || !IsFinite(placement.HostWallStartParameter)
            || !IsFinite(placement.HostWallEndParameter)
            || !IsFinite(placement.HostWallCenterParameter)
            || !IsFinite(placement.CrossWallOffsetDrawingUnits))
        {
            return false;
        }

        var spanLength = Math.Abs(placement.EndOffsetDrawingUnits - placement.StartOffsetDrawingUnits);
        var lengthTolerance = Math.Max(0.01, placement.LengthDrawingUnits * 0.02);
        if (Math.Abs(spanLength - placement.LengthDrawingUnits) > lengthTolerance)
        {
            return false;
        }

        var minOffset = Math.Min(placement.StartOffsetDrawingUnits, placement.EndOffsetDrawingUnits);
        var maxOffset = Math.Max(placement.StartOffsetDrawingUnits, placement.EndOffsetDrawingUnits);
        if (placement.CenterOffsetDrawingUnits < minOffset - lengthTolerance
            || placement.CenterOffsetDrawingUnits > maxOffset + lengthTolerance)
        {
            return false;
        }

        var parameterTolerance = Math.Max(0.01, 2.0 / Math.Max(placement.ReferenceLine.Length, 1));
        if (Math.Min(placement.HostWallStartParameter, placement.HostWallEndParameter) < -parameterTolerance
            || Math.Max(placement.HostWallStartParameter, placement.HostWallEndParameter) > 1 + parameterTolerance
            || placement.HostWallCenterParameter < -parameterTolerance
            || placement.HostWallCenterParameter > 1 + parameterTolerance)
        {
            return false;
        }

        return true;
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    public static IReadOnlyList<WallEvidenceWallAssessment> QueuedWallEvidenceReviews(WallEvidenceMap evidenceMap)
    {
        ArgumentNullException.ThrowIfNull(evidenceMap);

        return evidenceMap.WallAssessments
            .Where(IsActionableWallEvidenceReview)
            .OrderBy(assessment => assessment.PageNumber)
            .ThenByDescending(WallEvidenceReviewPriorityScore)
            .ThenBy(assessment => assessment.Bounds.Top)
            .ThenBy(assessment => assessment.Bounds.Left)
            .ThenBy(assessment => assessment.WallId, StringComparer.Ordinal)
            .Take(WallEvidenceReviewQueueLimit)
            .ToArray();
    }

    public static bool IsActionableWallEvidenceReview(WallEvidenceWallAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        return assessment.Decision == WallEvidenceDecision.Review
            && assessment.RequiresReview
            && !assessment.RejectedAsNoise
            && !string.IsNullOrWhiteSpace(assessment.WallId);
    }

    public static DiagnosticSeverity WallEvidenceReviewSeverity(WallEvidenceWallAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        return !assessment.PlacementReady || assessment.Confidence.Value < 0.5
            ? DiagnosticSeverity.Warning
            : DiagnosticSeverity.Info;
    }

    public static double WallEvidenceReviewPriorityScore(WallEvidenceWallAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        var score = 0.0;
        if (!assessment.PlacementReady)
        {
            score += 120;
        }

        score += assessment.Category switch
        {
            WallEvidenceCategory.WeakSingleLine => 100,
            WallEvidenceCategory.RecoveredWallBody => 75,
            WallEvidenceCategory.Unknown => 60,
            WallEvidenceCategory.MediumWallBody => 35,
            _ => 15
        };

        score += Math.Clamp(1 - assessment.Confidence.Value, 0, 1) * 60;
        score += Math.Min(assessment.SourcePrimitiveIds.Count, 20) * 2;
        score += Math.Clamp(assessment.ScoreBreakdown.FragmentReviewPenalty, 0, 1) * 60;
        score += Math.Clamp(assessment.ScoreBreakdown.NoisePenalty, 0, 1) * 40;
        score -= Math.Clamp(assessment.ScoreBreakdown.StructuralSupportScore, 0, 1) * 20;
        return score;
    }

    public static string WallEvidenceReviewReason(WallEvidenceWallAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        var reasons = new List<string>
        {
            $"wall evidence category {assessment.Category}",
            $"decision {assessment.Decision}",
            $"confidence {assessment.Confidence.Value.ToString("0.###", CultureInfo.InvariantCulture)}"
        };

        if (!assessment.PlacementReady)
        {
            reasons.Add("not placement-ready");
        }

        if (assessment.SourcePrimitiveIds.Count > 0)
        {
            reasons.Add($"{assessment.SourcePrimitiveIds.Count.ToString(CultureInfo.InvariantCulture)} source primitive(s)");
        }

        if (assessment.ScoreBreakdown.FragmentReviewPenalty > 0)
        {
            reasons.Add($"fragment review penalty {assessment.ScoreBreakdown.FragmentReviewPenalty.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        if (assessment.ScoreBreakdown.NoisePenalty > 0)
        {
            reasons.Add($"noise penalty {assessment.ScoreBreakdown.NoisePenalty.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        if (assessment.ScoreBreakdown.NegativeEvidence.Count > 0)
        {
            reasons.Add($"{assessment.ScoreBreakdown.NegativeEvidence.Count.ToString(CultureInfo.InvariantCulture)} negative evidence item(s)");
        }

        return string.Join("; ", reasons);
    }

    public static IReadOnlyList<PlanDiagnostic> QueuedWallGraphGapDiagnostics(IEnumerable<PlanDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return diagnostics
            .Where(diagnostic => IsWallGraphGapDiagnostic(diagnostic.Code))
            .OrderBy(diagnostic => diagnostic.PageNumber ?? int.MaxValue)
            .ThenBy(WallGraphGapDistance)
            .ThenBy(diagnostic => WallGraphGapKindPriority(diagnostic))
            .ThenBy(diagnostic => diagnostic.Properties.TryGetValue("nodeId", out var nodeId) ? nodeId : string.Empty, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Properties.TryGetValue("hostWallId", out var hostWallId) ? hostWallId : string.Empty, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Properties.TryGetValue("targetNodeId", out var targetNodeId) ? targetNodeId : string.Empty, StringComparer.Ordinal)
            .Take(WallGraphGapReviewQueueLimit)
            .ToArray();
    }

    public static IReadOnlyList<PlanDiagnostic> QueuedSurfacePatternWallOverlapDiagnostics(IEnumerable<PlanDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return diagnostics
            .Where(diagnostic => IsSurfacePatternWallOverlapDiagnostic(diagnostic.Code))
            .OrderBy(diagnostic => diagnostic.PageNumber ?? int.MaxValue)
            .ThenByDescending(SurfacePatternWallOverlapPriorityScore)
            .ThenBy(diagnostic => diagnostic.Properties.TryGetValue("surfacePatternId", out var patternId) ? patternId : string.Empty, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Properties.TryGetValue("wallId", out var wallId) ? wallId : string.Empty, StringComparer.Ordinal)
            .Take(SurfacePatternWallOverlapReviewQueueLimit)
            .ToArray();
    }

    public static string SurfacePatternWallOverlapReviewReason(PlanDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var reasons = new List<string>();
        if (diagnostic.Properties.TryGetValue("surfacePatternId", out var patternId) && !string.IsNullOrWhiteSpace(patternId))
        {
            reasons.Add($"surface pattern {patternId}");
        }

        if (diagnostic.Properties.TryGetValue("wallId", out var wallId) && !string.IsNullOrWhiteSpace(wallId))
        {
            reasons.Add($"wall {wallId}");
        }

        if (diagnostic.Properties.TryGetValue("wallComponentKind", out var componentKind) && !string.IsNullOrWhiteSpace(componentKind))
        {
            reasons.Add($"component {componentKind}");
        }

        if (diagnostic.Properties.TryGetValue("sharedSourcePrimitiveCount", out var sharedCount) && !string.IsNullOrWhiteSpace(sharedCount))
        {
            reasons.Add($"{sharedCount} shared source primitive(s)");
        }

        if (TryGetInvariantDouble(diagnostic, "wallOverlapRatio", out var overlapRatio))
        {
            reasons.Add($"wall overlap {overlapRatio.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        return reasons.Count == 0
            ? "surface-pattern/wall overlap requires review"
            : string.Join("; ", reasons);
    }

    public static double SurfacePatternWallOverlapPriorityScore(PlanDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var score = 0.0;
        if (TryGetInvariantDouble(diagnostic, "wallOverlapRatio", out var wallOverlapRatio))
        {
            score += Math.Clamp(wallOverlapRatio, 0, 1) * 100;
        }

        if (TryGetInvariantDouble(diagnostic, "patternOverlapRatio", out var patternOverlapRatio))
        {
            score += Math.Clamp(patternOverlapRatio, 0, 1) * 25;
        }

        if (diagnostic.Properties.TryGetValue("sharedSourcePrimitiveCount", out var sharedCountText)
            && int.TryParse(sharedCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sharedCount))
        {
            score += Math.Min(sharedCount, 10) * 30;
        }

        if (diagnostic.Properties.TryGetValue("wallComponentKind", out var componentKind)
            && string.Equals(componentKind, WallGraphComponentKind.MainStructural.ToString(), StringComparison.Ordinal))
        {
            score += 20;
        }

        score += diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => 100,
            DiagnosticSeverity.Warning => 50,
            _ => 0
        };

        return score;
    }

    public static string WallGraphGapReviewReason(PlanDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var reasons = new List<string>();
        if (diagnostic.Properties.TryGetValue("gapKind", out var gapKind) && !string.IsNullOrWhiteSpace(gapKind))
        {
            reasons.Add(gapKind);
        }
        else if (diagnostic.Properties.TryGetValue("overrunKind", out var overrunKind) && !string.IsNullOrWhiteSpace(overrunKind))
        {
            reasons.Add(overrunKind);
        }

        if (TryGetWallGraphGapDistance(diagnostic, out var distance))
        {
            var distanceLabel = string.Equals(diagnostic.Code, "wall_graph.endpoint_overrun.review", StringComparison.Ordinal)
                ? "overrun"
                : "gap";
            reasons.Add($"{distanceLabel} {distance.ToString("0.###", CultureInfo.InvariantCulture)} drawing unit(s)");
        }

        if (diagnostic.Properties.TryGetValue("wallIds", out var wallIds) && !string.IsNullOrWhiteSpace(wallIds))
        {
            reasons.Add($"walls {wallIds}");
        }

        return reasons.Count == 0
            ? string.Equals(diagnostic.Code, "wall_graph.endpoint_overrun.review", StringComparison.Ordinal)
                ? "possible overextended wall graph endpoint"
                : "possible unsnapped wall graph endpoint gap"
            : string.Join("; ", reasons);
    }

    public static double WallGraphGapPriorityScore(PlanDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var distance = WallGraphGapDistance(diagnostic);
        var score = Math.Max(0, 1000 - Math.Min(distance, 1000));
        if (WallGraphGapKindPriority(diagnostic) == 0)
        {
            score += 25;
        }

        score += diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => 100,
            DiagnosticSeverity.Warning => 50,
            _ => 0
        };

        return score;
    }

    public static IReadOnlyList<ObjectCandidateGroup> QueuedObjectGroups(IEnumerable<ObjectCandidateGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        return groups
            .Where(IsActionableObjectGroupReview)
            .OrderByDescending(ObjectGroupReviewPriorityScore)
            .ThenByDescending(group => group.Count)
            .ThenByDescending(group => group.Confidence.Value)
            .ThenBy(group => group.Id, StringComparer.Ordinal)
            .Take(ObjectGroupReviewQueueLimit)
            .ToArray();
    }

    public static bool IsActionableObjectGroupReview(ObjectCandidateGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return group.RequiresReview
            && (group.Count >= 2
                || group.DetectedTags.Count > 0
                || group.NearbyText.Count > 0
                || !string.IsNullOrWhiteSpace(group.Label)
                || !string.IsNullOrWhiteSpace(group.SymbolName)
                || group.VisualAi is not null);
    }

    public static int ObjectGroupReviewPriorityScore(ObjectCandidateGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var score = Math.Min(Math.Max(group.Count, 0), 50) * 8;
        score += Math.Min(group.DetectedTags.Count, 10) * 80;
        score += Math.Min(group.NearbyText.Count, 10) * 24;

        if (!string.IsNullOrWhiteSpace(group.Label))
        {
            score += 60;
        }

        if (!string.IsNullOrWhiteSpace(group.SymbolName))
        {
            score += 40;
        }

        if (group.VisualAi is not null)
        {
            score += 40;
        }

        if (group.Category is not ObjectCategory.Unknown and not ObjectCategory.GenericSymbol)
        {
            score += 20;
        }

        score += (int)Math.Round(Math.Clamp(group.Confidence.Value, 0, 1) * 10);
        return score;
    }

    public static string ObjectGroupReviewReason(ObjectCandidateGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var reasons = new List<string>();

        if (group.Count >= 2)
        {
            reasons.Add($"repeated {group.Count.ToString(CultureInfo.InvariantCulture)} occurrence(s)");
        }

        if (group.DetectedTags.Count > 0)
        {
            reasons.Add($"{group.DetectedTags.Count.ToString(CultureInfo.InvariantCulture)} detected tag(s)");
        }

        if (group.NearbyText.Count > 0)
        {
            reasons.Add($"{group.NearbyText.Count.ToString(CultureInfo.InvariantCulture)} nearby text item(s)");
        }

        if (!string.IsNullOrWhiteSpace(group.Label))
        {
            reasons.Add("draft label present");
        }

        if (!string.IsNullOrWhiteSpace(group.SymbolName))
        {
            reasons.Add("symbol name present");
        }

        if (group.VisualAi is not null)
        {
            reasons.Add("visual AI evidence present");
        }

        return reasons.Count == 0
            ? "review-required object group"
            : string.Join("; ", reasons);
    }

    public static bool IsSuppressedWallPatternDiagnostic(string? code) =>
        string.Equals(code, "walls.dense_orthogonal_pattern_filtered", StringComparison.Ordinal);

    public static bool IsSurfacePatternWallOverlapDiagnostic(string? code) =>
        string.Equals(code, "wall_graph.surface_pattern_wall_overlap.review", StringComparison.Ordinal);

    public static bool IsWallGraphGapDiagnostic(string? code) =>
        string.Equals(code, "wall_graph.endpoint_gap.review", StringComparison.Ordinal)
        || string.Equals(code, "wall_graph.endpoint_overrun.review", StringComparison.Ordinal);

    private static double WallGraphGapDistance(PlanDiagnostic diagnostic) =>
        TryGetWallGraphGapDistance(diagnostic, out var distance)
            ? distance
            : double.MaxValue;

    private static bool TryGetWallGraphGapDistance(PlanDiagnostic diagnostic, out double distance)
    {
        distance = 0;
        return (diagnostic.Properties.TryGetValue("gapDistance", out var value)
                || diagnostic.Properties.TryGetValue("overrunDistance", out value))
            && double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out distance);
    }

    private static bool TryGetInvariantDouble(PlanDiagnostic diagnostic, string propertyName, out double value)
    {
        value = 0;
        return diagnostic.Properties.TryGetValue(propertyName, out var text)
            && double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static int WallGraphGapKindPriority(PlanDiagnostic diagnostic) =>
        diagnostic.Properties.TryGetValue("gapKind", out var gapKind)
        && string.Equals(gapKind, "EndpointToWall", StringComparison.Ordinal)
            ? 0
            : diagnostic.Properties.TryGetValue("overrunKind", out var overrunKind)
              && string.Equals(overrunKind, "EndpointOverrun", StringComparison.Ordinal)
                ? 0
                : 1;

    private sealed class ScanReviewQueueSummaryBuilder
    {
        private readonly Dictionary<string, int> kindCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> severityCounts = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string kind, DiagnosticSeverity severity)
        {
            kindCounts[kind] = kindCounts.TryGetValue(kind, out var kindCount) ? kindCount + 1 : 1;

            var severityKey = severity.ToString();
            severityCounts[severityKey] = severityCounts.TryGetValue(severityKey, out var severityCount)
                ? severityCount + 1
                : 1;
        }

        public ScanReviewQueueSummary ToSummary() =>
            new(
                kindCounts.Values.Sum(),
                kindCounts
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                severityCounts
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
    }
}
