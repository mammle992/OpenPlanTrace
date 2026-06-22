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
            ? RecoverMissingWalls(context, cancellationToken)
            : Array.Empty<WallSegment>();
        var candidateWalls = context.WallCandidates.Concat(recoveredWalls).ToArray();

        var assessments = new List<WallEvidenceWallAssessment>();
        var segments = new List<WallEvidenceSegment>();
        var bands = new List<WallEvidenceBand>();
        var rejectedWallIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var wall in candidateWalls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var assessment = AssessWall(wall, context, candidateWalls);
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
            assessments.ToArray(),
            originalWallCount,
            recoveredWalls.Count);

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

    private static WallEvidenceWallAssessment AssessWall(
        WallSegment wall,
        ScanContext context,
        IReadOnlyList<WallSegment> candidateWalls)
    {
        var evidence = new List<string>();
        var category = WallEvidenceCategory.Unknown;
        var confidence = wall.Confidence;
        var placementReady = false;
        var requiresReview = false;
        var rejected = false;

        if (TryClassifyRecoveredDuplicateWallBodyReview(wall, context, candidateWalls, out var recoveredDuplicateEvidence))
        {
            category = WallEvidenceCategory.MediumWallBody;
            confidence = new Confidence(Math.Min(0.88, Math.Max(wall.Confidence.Value, 0.68)));
            placementReady = false;
            requiresReview = true;
            evidence.Add(recoveredDuplicateEvidence);
        }
        else if (wall.Evidence.Any(item => item.Contains("recovered by wall evidence map", StringComparison.OrdinalIgnoreCase)))
        {
            var shortUnlayeredRecoveredSegment = IsShortUnlayeredRecoveredSegment(wall, context);
            category = WallEvidenceCategory.RecoveredWallBody;
            confidence = new Confidence(Math.Max(wall.Confidence.Value, 0.68));
            placementReady = !shortUnlayeredRecoveredSegment && confidence.Value >= 0.72;
            requiresReview = !placementReady;
            evidence.Add(shortUnlayeredRecoveredSegment
                ? "wall evidence: short recovered unlayered/unknown wall segment requires review before exact placement"
                : "wall evidence: recovered wall body from unclaimed parallel-face evidence");
        }
        else if (TryClassifyPairedDoorOrOpeningSymbolNoise(wall, context, out var pairedDoorEvidence))
        {
            category = WallEvidenceCategory.DoorOrOpeningSymbol;
            confidence = new Confidence(Math.Min(0.95, Math.Max(wall.Confidence.Value, 0.74)));
            rejected = true;
            requiresReview = true;
            evidence.Add(pairedDoorEvidence);
        }
        else if (TryClassifyPairedSurfacePatternLayerNoise(wall, context, out var pairedSurfaceEvidence))
        {
            category = WallEvidenceCategory.SurfacePatternDetail;
            confidence = new Confidence(Math.Min(0.95, Math.Max(wall.Confidence.Value, 0.74)));
            rejected = true;
            requiresReview = true;
            evidence.Add(pairedSurfaceEvidence);
        }
        else if (TryClassifyPairedObjectOrFixtureLineworkNoise(wall, context, out var pairedObjectEvidence))
        {
            category = WallEvidenceCategory.ObjectOrFixtureDetail;
            confidence = new Confidence(Math.Min(0.94, Math.Max(wall.Confidence.Value, 0.74)));
            rejected = true;
            requiresReview = true;
            evidence.Add(pairedObjectEvidence);
        }
        else if (TryClassifyPairedDimensionOrAnnotationNoise(wall, context, out var pairedDimensionEvidence))
        {
            category = WallEvidenceCategory.DimensionOrAnnotation;
            confidence = new Confidence(Math.Min(0.92, Math.Max(wall.Confidence.Value, 0.72)));
            rejected = true;
            requiresReview = true;
            evidence.Add(pairedDimensionEvidence);
        }
        else if (TryClassifyOutdoorAreaBoundaryNoise(wall, context, out var outdoorBoundaryEvidence))
        {
            category = WallEvidenceCategory.SurfacePatternDetail;
            confidence = new Confidence(Math.Min(0.95, Math.Max(wall.Confidence.Value, 0.76)));
            rejected = true;
            requiresReview = true;
            evidence.Add(outdoorBoundaryEvidence);
        }
        else if (TryClassifyOutdoorAreaBoundaryReview(wall, context, out var outdoorBoundaryReviewEvidence))
        {
            category = WallEvidenceCategory.SurfacePatternDetail;
            confidence = new Confidence(Math.Min(0.88, Math.Max(wall.Confidence.Value, 0.68)));
            placementReady = false;
            requiresReview = true;
            evidence.Add(outdoorBoundaryReviewEvidence);
        }
        else if (TryClassifyContinuitySupportedPairedWall(wall, context, out var continuityEvidence))
        {
            category = WallEvidenceCategory.StrongWallBody;
            confidence = new Confidence(Math.Max(wall.Confidence.Value, 0.74));
            placementReady = true;
            evidence.Add(continuityEvidence);
        }
        else if (TryClassifyVeryShortLowScorePairedWallReview(wall, context, out var veryShortPairEvidence))
        {
            category = WallEvidenceCategory.MediumWallBody;
            confidence = new Confidence(Math.Max(wall.Confidence.Value, 0.62));
            placementReady = false;
            requiresReview = true;
            evidence.Add(veryShortPairEvidence);
        }
        else if (TryClassifyShortFragmentedPairedWallReview(wall, context, out var fragmentedPairEvidence))
        {
            category = WallEvidenceCategory.MediumWallBody;
            confidence = new Confidence(Math.Max(wall.Confidence.Value, 0.62));
            placementReady = false;
            requiresReview = true;
            evidence.Add(fragmentedPairEvidence);
        }
        else if (TryClassifyShortWeaklySupportedPairedWallReview(wall, context, out var shortPairEvidence))
        {
            category = WallEvidenceCategory.MediumWallBody;
            confidence = new Confidence(Math.Max(wall.Confidence.Value, 0.62));
            placementReady = false;
            requiresReview = true;
            evidence.Add(shortPairEvidence);
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
        else if (TryClassifyObjectOrFixtureLineworkNoise(wall, context, out var objectEvidence))
        {
            category = WallEvidenceCategory.ObjectOrFixtureDetail;
            confidence = new Confidence(Math.Min(0.94, Math.Max(wall.Confidence.Value, 0.70)));
            rejected = true;
            requiresReview = true;
            evidence.Add(objectEvidence);
        }
        else if (TryClassifyDimensionGeometryNoise(wall, context, out var dimensionGeometryEvidence))
        {
            category = WallEvidenceCategory.DimensionOrAnnotation;
            confidence = new Confidence(Math.Min(0.92, Math.Max(wall.Confidence.Value, 0.70)));
            rejected = true;
            requiresReview = true;
            evidence.Add(dimensionGeometryEvidence);
        }
        else if (TryClassifyDimensionOrAnnotationNoise(wall, context, out var dimensionEvidence))
        {
            category = WallEvidenceCategory.DimensionOrAnnotation;
            confidence = new Confidence(Math.Min(0.92, Math.Max(wall.Confidence.Value, 0.68)));
            rejected = true;
            requiresReview = true;
            evidence.Add(dimensionEvidence);
        }
        else if (TryClassifyUnpairedOutdoorAreaBoundaryReview(wall, context, out var unpairedOutdoorBoundaryEvidence))
        {
            category = WallEvidenceCategory.SurfacePatternDetail;
            confidence = new Confidence(Math.Min(0.84, Math.Max(wall.Confidence.Value, 0.64)));
            placementReady = false;
            requiresReview = true;
            evidence.Add(unpairedOutdoorBoundaryEvidence);
        }
        else if (TryClassifyRepeatedShortUnlayeredDetailReview(wall, context, out var repeatedDetailEvidence))
        {
            category = WallEvidenceCategory.ObjectOrFixtureDetail;
            confidence = new Confidence(Math.Min(0.88, Math.Max(wall.Confidence.Value, 0.68)));
            placementReady = false;
            requiresReview = true;
            evidence.Add(repeatedDetailEvidence);
        }
        else if (TryClassifyDuplicateWallFaceReview(wall, context, out var duplicateWallFaceEvidence))
        {
            category = WallEvidenceCategory.MediumWallBody;
            confidence = new Confidence(Math.Min(0.88, Math.Max(wall.Confidence.Value, 0.66)));
            placementReady = false;
            requiresReview = true;
            evidence.Add(duplicateWallFaceEvidence);
        }
        else if (TryClassifySparseUnlayeredFragmentMergedWallReview(wall, context, out var sparseFragmentEvidence))
        {
            category = WallEvidenceCategory.MediumWallBody;
            confidence = new Confidence(Math.Min(0.88, Math.Max(wall.Confidence.Value, 0.64)));
            placementReady = false;
            requiresReview = true;
            evidence.Add(sparseFragmentEvidence);
        }
        else if (TryClassifyUnsupportedFragmentMergedWallReview(wall, context, out var unsupportedFragmentEvidence))
        {
            category = WallEvidenceCategory.MediumWallBody;
            confidence = new Confidence(Math.Min(0.88, Math.Max(wall.Confidence.Value, 0.64)));
            placementReady = false;
            requiresReview = true;
            evidence.Add(unsupportedFragmentEvidence);
        }
        else if (IsMediumWallBody(wall, context))
        {
            category = WallEvidenceCategory.MediumWallBody;
            confidence = new Confidence(Math.Max(wall.Confidence.Value, 0.62));
            placementReady = wall.FragmentEvidence?.RequiresGeometryReview != true;
            requiresReview = !placementReady;
            if (placementReady && IsShortUnknownFragmentMergedWall(wall, context))
            {
                placementReady = false;
                requiresReview = true;
                evidence.Add("wall evidence: short unknown fragment-merged wall candidate requires review before exact placement");
            }
            evidence.Add("wall evidence: medium wall body from wall-like layer, length, or structural context");
        }
        else
        {
            category = WallEvidenceCategory.WeakSingleLine;
            confidence = new Confidence(Math.Min(wall.Confidence.Value, 0.58));
            requiresReview = true;
            if (IsShortUnlayeredSingleLineCandidate(wall, context))
            {
                var structuralEndpointSupportCount = CountStructuralEndpointSupport(
                    wall.CenterLine,
                    wall.PageNumber,
                    context.WallCandidates,
                    context.Options);
                var distinctStructuralSupportWallCount = CountDistinctStructuralSupportWalls(
                    wall.CenterLine,
                    wall.PageNumber,
                    context.WallCandidates,
                    context.Options);
                if (structuralEndpointSupportCount == 1)
                {
                    evidence.Add("wall evidence: short unlayered single-line candidate has only one structural endpoint support; keep for topology but review before exact placement");
                }
                else if (structuralEndpointSupportCount >= 2 && distinctStructuralSupportWallCount < 2)
                {
                    evidence.Add("wall evidence: short unlayered single-line candidate endpoints are only supported by one distinct structural wall; keep for topology but review before exact placement");
                }
                else
                {
                    evidence.Add("wall evidence: weak short unlayered single-line wall candidate; keep for topology but review before exact placement");
                }
            }
            else
            {
                evidence.Add("wall evidence: weak single-line wall candidate; keep for topology but review before exact placement");
            }
        }

        if (wall.FragmentEvidence?.RequiresGeometryReview == true)
        {
            placementReady = false;
            requiresReview = true;
            evidence.Add("wall evidence: fragment-merged geometry requires review before exact placement");
        }

        var decision = DetermineDecision(placementReady, requiresReview, rejected);
        var scoreBreakdown = BuildScoreBreakdown(wall, context, category, placementReady, requiresReview, rejected);

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
            AppendEvidence(wall.Evidence, evidence))
        {
            Decision = decision,
            ScoreBreakdown = scoreBreakdown
        };
    }

    private static WallEvidenceDecision DetermineDecision(
        bool placementReady,
        bool requiresReview,
        bool rejected)
    {
        if (rejected)
        {
            return WallEvidenceDecision.Reject;
        }

        if (placementReady && !requiresReview)
        {
            return WallEvidenceDecision.Accept;
        }

        return WallEvidenceDecision.Review;
    }

    private static WallEvidenceScoreBreakdown BuildScoreBreakdown(
        WallSegment wall,
        ScanContext context,
        WallEvidenceCategory category,
        bool placementReady,
        bool requiresReview,
        bool rejected)
    {
        var positiveEvidence = new List<string>();
        var negativeEvidence = new List<string>();

        var pairSupportScore = 0.0;
        if (IsStrongPairedWall(wall))
        {
            pairSupportScore = 0.50;
            positiveEvidence.Add("strong parallel-face wall pair");
        }
        else if (category == WallEvidenceCategory.RecoveredWallBody)
        {
            pairSupportScore = 0.35;
            positiveEvidence.Add("recovered parallel-face wall band");
        }
        else if (wall.PairEvidence is not null)
        {
            pairSupportScore = 0.32;
            positiveEvidence.Add("parallel-face wall pair");
        }
        else if (category == WallEvidenceCategory.MediumWallBody)
        {
            pairSupportScore = 0.22;
            positiveEvidence.Add("medium wall-body geometry");
        }
        else if (category == WallEvidenceCategory.WeakSingleLine)
        {
            pairSupportScore = 0.08;
            positiveEvidence.Add("weak single-line wall-like geometry");
        }

        var layerSupportScore = 0.0;
        if (IsWallLayerBacked(wall, context))
        {
            layerSupportScore = 0.25;
            positiveEvidence.Add("wall or structural source layer");
        }

        var endpointSupportCount = CountStructuralEndpointSupport(
            wall.CenterLine,
            wall.PageNumber,
            context.WallCandidates,
            context.Options);
        var structuralSupportScore = Math.Min(0.20, endpointSupportCount * 0.10);
        if (endpointSupportCount > 0)
        {
            positiveEvidence.Add(endpointSupportCount == 1
                ? "one endpoint supported by structural context"
                : "both endpoints supported by structural context");
        }

        var recoverySupportScore = 0.0;
        if (category == WallEvidenceCategory.RecoveredWallBody
            || wall.Evidence.Any(item => item.Contains("recovered by wall evidence map", StringComparison.OrdinalIgnoreCase)))
        {
            recoverySupportScore = 0.20;
            positiveEvidence.Add("missing-wall recovery evidence");
        }

        var noisePenalty = 0.0;
        if (rejected)
        {
            noisePenalty = category switch
            {
                WallEvidenceCategory.DoorOrOpeningSymbol => 0.90,
                WallEvidenceCategory.SurfacePatternDetail => 0.85,
                WallEvidenceCategory.DimensionOrAnnotation => 0.80,
                WallEvidenceCategory.ObjectOrFixtureDetail => 0.75,
                _ => 0.65
            };
            negativeEvidence.Add($"explicit non-wall evidence: {category}");
        }
        else if (category == WallEvidenceCategory.WeakSingleLine)
        {
            noisePenalty = 0.12;
            negativeEvidence.Add("weak single-line wall evidence needs review");
        }

        var fragmentReviewPenalty = 0.0;
        if (wall.FragmentEvidence?.RequiresGeometryReview == true)
        {
            fragmentReviewPenalty = 0.25;
            negativeEvidence.Add("fragment-merged geometry requires review");
        }

        if (requiresReview && !placementReady && !rejected && negativeEvidence.Count == 0)
        {
            negativeEvidence.Add("not placement-ready without review");
        }

        var positiveScore = Clamp01(pairSupportScore + layerSupportScore + structuralSupportScore + recoverySupportScore);
        var negativeScore = Clamp01(noisePenalty + fragmentReviewPenalty);
        var decisionScore = Math.Max(-1, Math.Min(1, positiveScore - negativeScore));

        return new WallEvidenceScoreBreakdown(
            RoundScore(positiveScore),
            RoundScore(negativeScore),
            RoundScore(decisionScore),
            RoundScore(pairSupportScore),
            RoundScore(layerSupportScore),
            RoundScore(structuralSupportScore),
            RoundScore(recoverySupportScore),
            RoundScore(noisePenalty),
            RoundScore(fragmentReviewPenalty),
            positiveEvidence.ToArray(),
            negativeEvidence.ToArray());
    }

    private static double Clamp01(double value) =>
        Math.Max(0, Math.Min(1, value));

    private static double RoundScore(double value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static bool IsStrongPairedWall(WallSegment wall) =>
        wall.DetectionKind == WallDetectionKind.ParallelLinePair
        && wall.PairEvidence is { Score: >= 0.62, OverlapRatio: >= 0.55 };

    private static bool TryClassifyContinuitySupportedPairedWall(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (!IsStrongPairedWall(wall)
            || wall.PairEvidence is null
            || wall.FragmentEvidence?.RequiresGeometryReview == true
            || wall.DrawingLength > ShortWeaklySupportedPairedWallReviewLength(context.Options))
        {
            return false;
        }

        if (wall.WallType == WallType.Unknown && !IsWallLayerBacked(wall, context))
        {
            return false;
        }

        var structuralSupportWallCount = CountDistinctStructuralSupportWalls(
            wall.CenterLine,
            wall.PageNumber,
            context.WallCandidates,
            context.Options);
        if (structuralSupportWallCount <= 0)
        {
            return false;
        }

        var continuitySupportCount = CountCollinearContinuitySupportWalls(wall, context);
        if (continuitySupportCount <= 0)
        {
            return false;
        }

        evidence = $"wall evidence: continuity-supported short paired wall body; {continuitySupportCount.ToString(CultureInfo.InvariantCulture)} collinear structural continuation wall(s)";
        return true;
    }

    private static bool TryClassifyVeryShortLowScorePairedWallReview(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is null
            || IsWallLayerBacked(wall, context)
            || wall.FragmentEvidence?.RequiresGeometryReview == true
            || wall.DrawingLength > VeryShortLowScorePairedWallReviewLength(context.Options)
            || wall.PairEvidence.Score >= 0.78)
        {
            return false;
        }

        evidence = string.Format(
            CultureInfo.InvariantCulture,
            "wall evidence: very short unlayered parallel-face candidate has low pair score {0:0.###}; keep for topology but block exact placement until reviewed",
            wall.PairEvidence.Score);
        return true;
    }

    private static double VeryShortLowScorePairedWallReviewLength(ScannerOptions options) =>
        Math.Max(options.MinWallLength * 1.15, options.DefaultWallThickness * 7.0);

    private static bool TryClassifyShortFragmentedPairedWallReview(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is null
            || IsWallLayerBacked(wall, context)
            || wall.FragmentEvidence?.RequiresGeometryReview == true
            || wall.DrawingLength > ShortFragmentedPairedWallReviewLength(context.Options))
        {
            return false;
        }

        var pair = wall.PairEvidence;
        if (pair.Score >= 0.72)
        {
            return false;
        }

        var evidenceFragmentCounts = ReadFaceFragmentCountsFromEvidence(wall.Evidence);
        var maxFaceFragments = Math.Max(
            Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount),
            evidenceFragmentCounts.MaxFaceFragments);
        var totalFaceFragments = Math.Max(
            pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount,
            evidenceFragmentCounts.TotalFaceFragments);
        if (maxFaceFragments < 24 && totalFaceFragments < 36)
        {
            return false;
        }

        evidence = string.Format(
            CultureInfo.InvariantCulture,
            "wall evidence: short unlayered parallel-face candidate has noisy fragmented face evidence (score {0:0.###}, max face fragments {1}, total face fragments {2}); keep for topology but block exact placement until reviewed",
            pair.Score,
            maxFaceFragments,
            totalFaceFragments);
        return true;
    }

    private static double ShortFragmentedPairedWallReviewLength(ScannerOptions options) =>
        Math.Max(options.MinWallLength * 3.0, options.DefaultWallThickness * 18.0);

    private static FaceFragmentCounts ReadFaceFragmentCountsFromEvidence(IEnumerable<string> evidence)
    {
        var maxFaceFragments = 0;
        var totalFaceFragments = 0;
        foreach (var item in evidence)
        {
            if (string.IsNullOrWhiteSpace(item)
                || !item.Contains("face merged", StringComparison.OrdinalIgnoreCase)
                || !TryReadIntegerBeforeMarker(item, "fragments", out var fragments))
            {
                continue;
            }

            maxFaceFragments = Math.Max(maxFaceFragments, fragments);
            totalFaceFragments += fragments;
        }

        return new FaceFragmentCounts(maxFaceFragments, totalFaceFragments);
    }

    private static bool TryReadIntegerBeforeMarker(
        string value,
        string marker,
        out int parsed)
    {
        var markerIndex = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
        {
            parsed = 0;
            return false;
        }

        var end = markerIndex - 1;
        while (end >= 0 && char.IsWhiteSpace(value[end]))
        {
            end--;
        }

        var start = end;
        while (start >= 0 && char.IsDigit(value[start]))
        {
            start--;
        }

        start++;
        if (start > end)
        {
            parsed = 0;
            return false;
        }

        return int.TryParse(
            value[start..(end + 1)],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out parsed);
    }

    private static bool TryClassifyShortWeaklySupportedPairedWallReview(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is null
            || IsWallLayerBacked(wall, context))
        {
            return false;
        }

        var reviewLength = ShortWeaklySupportedPairedWallReviewLength(context.Options);
        if (wall.DrawingLength > reviewLength)
        {
            return false;
        }

        var structuralSupportWallCount = CountDistinctStructuralSupportWalls(
            wall.CenterLine,
            wall.PageNumber,
            context.WallCandidates,
            context.Options);
        var structuralEndpointSupportCount = CountStructuralEndpointSupport(
            wall.CenterLine,
            wall.PageNumber,
            context.WallCandidates,
            context.Options);
        if (structuralEndpointSupportCount >= 2 && structuralSupportWallCount >= 2)
        {
            return false;
        }

        var weakPairEvidence = wall.PairEvidence.Score < 0.68
            || wall.PairEvidence.FirstFaceFragmentCount + wall.PairEvidence.SecondFaceFragmentCount >= 6;
        if (!IsStrongPairedWall(wall) && !weakPairEvidence)
        {
            return false;
        }

        var supportEvidence = structuralEndpointSupportCount <= 0
            ? "has no structural endpoint support"
            : structuralEndpointSupportCount == 1
                ? "has only one structurally supported endpoint"
                : "has clustered support but fewer than two distinct structural wall connections";
        var pairEvidence = weakPairEvidence
            ? $"weak/fragmented pair evidence (score {wall.PairEvidence.Score.ToString("0.###", CultureInfo.InvariantCulture)}, {wall.PairEvidence.FirstFaceFragmentCount + wall.PairEvidence.SecondFaceFragmentCount} face fragments)"
            : "short paired wall evidence";
        evidence = $"wall evidence: short unlayered parallel-face candidate {supportEvidence} and {pairEvidence}; keep for topology but block exact placement until reviewed";
        return true;
    }

    private static double ShortWeaklySupportedPairedWallReviewLength(ScannerOptions options) =>
        Math.Max(options.MinWallLength * 2.25, options.DefaultWallThickness * 14.0);

    private static bool TryClassifyRecoveredDuplicateWallBodyReview(
        WallSegment wall,
        ScanContext context,
        IReadOnlyList<WallSegment> candidateWalls,
        out string evidence)
    {
        evidence = string.Empty;
        if (!IsStrongPairedWall(wall)
            || wall.PairEvidence is null
            || wall.FragmentEvidence?.RequiresGeometryReview == true
            || !IsRecoveredWallEvidence(wall))
        {
            return false;
        }

        foreach (var other in candidateWalls)
        {
            var otherIsRecovered = IsRecoveredWallEvidence(other);
            if (string.Equals(other.Id, wall.Id, StringComparison.Ordinal)
                || other.PageNumber != wall.PageNumber
                || !IsStrongPairedWall(other)
                || other.PairEvidence is null
                || other.FragmentEvidence?.RequiresGeometryReview == true
                || (!otherIsRecovered && other.WallType == WallType.Unknown)
                || (!otherIsRecovered && other.DrawingLength < MinimumRecoveredDuplicateRepresentativeLength(wall))
                || (otherIsRecovered && !IsPreferredRecoveredDuplicateRepresentative(other, wall))
                || !AreNearParallel(wall.CenterLine, other.CenterLine))
            {
                continue;
            }

            var overlapRatio = AxisAlignedOverlapRatio(wall.CenterLine, other.CenterLine);
            if (overlapRatio < 0.72)
            {
                continue;
            }

            var separation = CenterLineSeparation(wall.CenterLine, other.CenterLine);
            var maxDuplicateSeparation = RecoveredDuplicateWallBodySeparationLimit(
                wall.PairEvidence,
                other.PairEvidence,
                context.Options);
            if (separation > maxDuplicateSeparation)
            {
                continue;
            }

            evidence = $"wall evidence: recovered duplicate wall body already represented by stronger nearby paired wall body {other.Id}; keep for review but block exact placement";
            return true;
        }

        return false;
    }

    private static double MinimumRecoveredDuplicateRepresentativeLength(WallSegment recoveredWall) =>
        Math.Max(recoveredWall.DrawingLength * 0.72, recoveredWall.DrawingLength - 24.0);

    private static bool IsRecoveredWallEvidence(WallSegment wall) =>
        wall.Evidence.Any(item => item.Contains("recovered by wall evidence map", StringComparison.OrdinalIgnoreCase));

    private static bool IsPreferredRecoveredDuplicateRepresentative(WallSegment candidate, WallSegment duplicate)
    {
        if (!IsRecoveredWallEvidence(candidate))
        {
            return false;
        }

        var scoreDelta = candidate.Confidence.Value - duplicate.Confidence.Value;
        if (Math.Abs(scoreDelta) > 0.01)
        {
            return scoreDelta > 0;
        }

        var lengthDelta = candidate.DrawingLength - duplicate.DrawingLength;
        if (Math.Abs(lengthDelta) > 1.0)
        {
            return lengthDelta > 0;
        }

        return string.Compare(candidate.Id, duplicate.Id, StringComparison.Ordinal) < 0;
    }

    private static double RecoveredDuplicateWallBodySeparationLimit(
        WallPairEvidence first,
        WallPairEvidence second,
        ScannerOptions options)
    {
        var pairEnvelope = (first.FaceSeparation + second.FaceSeparation) * 0.62;
        var optionEnvelope = Math.Max(options.WallSnapTolerance * 2.5, options.DefaultWallThickness * 2.25);
        return Math.Max(pairEnvelope, optionEnvelope);
    }

    private static double CenterLineSeparation(PlanLineSegment first, PlanLineSegment second)
    {
        if (first.IsHorizontal() && second.IsHorizontal())
        {
            return Math.Abs(first.Midpoint.Y - second.Midpoint.Y);
        }

        if (first.IsVertical() && second.IsVertical())
        {
            return Math.Abs(first.Midpoint.X - second.Midpoint.X);
        }

        return Math.Min(
            first.DistanceToPoint(second.Midpoint),
            second.DistanceToPoint(first.Midpoint));
    }

    private static int CountCollinearContinuitySupportWalls(
        WallSegment wall,
        ScanContext context)
    {
        var options = context.Options;
        var collinearTolerance = Math.Max(options.WallSnapTolerance * 2.0, options.DefaultWallThickness * 1.5);
        var maxGap = Math.Max(options.MaxOpeningGap * 1.15, options.MinWallLength * 0.65);
        var minimumSupportLength = Math.Max(options.MinWallLength * 0.75, wall.DrawingLength * 0.9);
        var supportIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var other in context.WallCandidates)
        {
            if (string.Equals(other.Id, wall.Id, StringComparison.Ordinal)
                || other.PageNumber != wall.PageNumber
                || other.PairEvidence is null
                || other.DrawingLength < minimumSupportLength
                || !IsStrongPairedWall(other)
                || !AreNearParallel(wall.CenterLine, other.CenterLine)
                || !AreNearCollinear(wall.CenterLine, other.CenterLine, collinearTolerance)
                || SameLine(wall.CenterLine, other.CenterLine, options.WallSnapTolerance))
            {
                continue;
            }

            if (wall.WallType != WallType.Unknown
                && other.WallType != WallType.Unknown
                && other.WallType != wall.WallType)
            {
                continue;
            }

            var overlapRatio = AxisAlignedOverlapRatio(wall.CenterLine, other.CenterLine);
            if (overlapRatio >= 0.75 && other.DrawingLength <= wall.DrawingLength * 1.25)
            {
                continue;
            }

            var gap = CollinearGap(wall.CenterLine, other.CenterLine);
            if (gap <= maxGap)
            {
                supportIds.Add(other.Id);
            }
        }

        return supportIds.Count;
    }

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

        var endpointSupportCount = CountStructuralEndpointSupport(
            wall.CenterLine,
            wall.PageNumber,
            context.WallCandidates,
            context.Options);
        if (endpointSupportCount <= 0)
        {
            return false;
        }

        if (!IsShortUnlayeredSingleLineCandidate(wall, context))
        {
            return true;
        }

        return endpointSupportCount >= 2
            && CountDistinctStructuralSupportWalls(
                wall.CenterLine,
                wall.PageNumber,
                context.WallCandidates,
                context.Options) >= 2;
    }

    private static bool IsShortUnlayeredRecoveredSegment(WallSegment wall, ScanContext context) =>
        wall.WallType == WallType.Unknown
        && !IsWallLayerBacked(wall, context)
        && wall.Evidence.Any(item => item.Contains("short supported wall segment", StringComparison.OrdinalIgnoreCase));

    private static bool IsShortUnknownFragmentMergedWall(WallSegment wall, ScanContext context) =>
        wall.WallType == WallType.Unknown
        && !IsWallLayerBacked(wall, context)
        && wall.DetectionKind == WallDetectionKind.FragmentMerged
        && wall.DrawingLength < ShortUnknownFragmentMergedReviewLength(context.Options);

    private static double ShortUnknownFragmentMergedReviewLength(ScannerOptions options) =>
        Math.Max(options.MinWallLength, options.DefaultWallThickness * 10.0);

    private static bool TryClassifyUnsupportedFragmentMergedWallReview(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.DetectionKind != WallDetectionKind.FragmentMerged
            || wall.PairEvidence is not null
            || wall.FragmentEvidence is not { RequiresGeometryReview: false } fragmentEvidence
            || IsWallLayerBacked(wall, context)
            || IsRecoveredWallEvidence(wall)
            || wall.WallType == WallType.Exterior
            || wall.DrawingLength < UnsupportedFragmentMergedReviewLength(context.Options))
        {
            return false;
        }

        var visiblyFragmented = fragmentEvidence.FragmentCount >= 3
            || fragmentEvidence.GapRatio >= 0.08
            || fragmentEvidence.TotalHealedGap >= context.Options.DefaultWallThickness * 1.5;
        if (!visiblyFragmented)
        {
            return false;
        }

        var trustedEndpointSupportCount = CountTrustedStructuralEndpointSupport(
            wall.CenterLine,
            wall.PageNumber,
            context);
        var trustedSupportWallCount = CountDistinctTrustedStructuralSupportWalls(
            wall.CenterLine,
            wall.PageNumber,
            context);
        if (trustedEndpointSupportCount >= 2 && trustedSupportWallCount >= 2)
        {
            return false;
        }

        var supportEvidence = trustedEndpointSupportCount <= 0
            ? "no trusted structural endpoint support"
            : trustedEndpointSupportCount == 1
                ? "only one trusted structural endpoint"
                : "endpoint support from fewer than two trusted structural walls";
        evidence = string.Format(
            CultureInfo.InvariantCulture,
            "wall evidence: unlayered fragment-merged wall candidate has {0} ({1} fragments, gap ratio {2:0.###}); keep for topology but block exact placement until reviewed",
            supportEvidence,
            fragmentEvidence.FragmentCount,
            fragmentEvidence.GapRatio);
        return true;
    }

    private static bool TryClassifySparseUnlayeredFragmentMergedWallReview(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.DetectionKind != WallDetectionKind.FragmentMerged
            || wall.PairEvidence is not null
            || wall.FragmentEvidence is not { RequiresGeometryReview: false } fragmentEvidence
            || IsWallLayerBacked(wall, context)
            || IsRecoveredWallEvidence(wall)
            || wall.WallType == WallType.Exterior
            || wall.DrawingLength > SparseUnlayeredFragmentMergedReviewLength(context.Options))
        {
            return false;
        }

        var sourceFragmentCount = Math.Max(fragmentEvidence.FragmentCount, wall.SourcePrimitiveIds.Count);
        if (sourceFragmentCount > 2)
        {
            return false;
        }

        evidence = string.Format(
            CultureInfo.InvariantCulture,
            "wall evidence: sparse unlayered fragment-merged wall candidate has only {0} source fragment(s); keep for topology but block exact placement until reviewed as possible fixture/detail linework",
            sourceFragmentCount);
        return true;
    }

    private static double SparseUnlayeredFragmentMergedReviewLength(ScannerOptions options) =>
        Math.Max(options.MinWallLength * 4.5, options.DefaultWallThickness * 40.0);

    private static double UnsupportedFragmentMergedReviewLength(ScannerOptions options) =>
        Math.Max(options.MinWallLength * 1.35, options.DefaultWallThickness * 12.0);

    private static bool IsShortUnlayeredSingleLineCandidate(WallSegment wall, ScanContext context) =>
        wall.DetectionKind == WallDetectionKind.SingleLine
        && wall.PairEvidence is null
        && wall.FragmentEvidence is null
        && !IsWallLayerBacked(wall, context)
        && wall.DrawingLength < ShortUnlayeredSingleLineReviewLength(context.Options);

    private static double ShortUnlayeredSingleLineReviewLength(ScannerOptions options) =>
        Math.Max(options.MinWallLength * 1.5, options.DefaultWallThickness * 12.0);

    private static bool TryClassifyRepeatedShortUnlayeredDetailReview(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.PairEvidence is not null
            || IsWallLayerBacked(wall, context)
            || wall.DetectionKind is not (WallDetectionKind.SingleLine or WallDetectionKind.FragmentMerged))
        {
            return false;
        }

        var orientation = ResolveAxisOrientation(wall.CenterLine);
        if (orientation == WallOrientation.Unknown
            || wall.DrawingLength > RepeatedShortUnlayeredDetailReviewLength(context.Options))
        {
            return false;
        }

        var similarCandidates = context.WallCandidates
            .Where(candidate => !string.Equals(candidate.Id, wall.Id, StringComparison.Ordinal))
            .Where(candidate => candidate.PageNumber == wall.PageNumber)
            .Where(candidate => candidate.PairEvidence is null)
            .Where(candidate => candidate.DetectionKind is WallDetectionKind.SingleLine or WallDetectionKind.FragmentMerged)
            .Where(candidate => candidate.DrawingLength <= RepeatedShortUnlayeredDetailReviewLength(context.Options))
            .Where(candidate => !IsWallLayerBacked(candidate, context))
            .Where(candidate => ResolveAxisOrientation(candidate.CenterLine) == orientation)
            .Where(candidate => LooksLikeRepeatedShortDetailNeighbor(wall.CenterLine, candidate.CenterLine, context.Options))
            .Take(4)
            .ToArray();

        if (similarCandidates.Length < 2)
        {
            return false;
        }

        evidence =
            $"wall evidence: repeated short unlayered {orientation.ToString().ToLowerInvariant()} linework group has {similarCandidates.Length + 1} similar candidates; review as detail/object linework before exact wall placement";
        return true;
    }

    private static double RepeatedShortUnlayeredDetailReviewLength(ScannerOptions options) =>
        Math.Max(options.MinWallLength * 1.85, options.DefaultWallThickness * 14.0);

    private static bool TryClassifyDuplicateWallFaceReview(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.DetectionKind == WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is not null
            || wall.FragmentEvidence?.RequiresGeometryReview == true)
        {
            return false;
        }

        var collinearityTolerance = Math.Max(
            context.Options.WallSnapTolerance * 2.5,
            context.Options.DefaultWallThickness * 1.35);
        foreach (var other in context.WallCandidates)
        {
            if (string.Equals(other.Id, wall.Id, StringComparison.Ordinal)
                || other.PageNumber != wall.PageNumber
                || other.PairEvidence is null
                || !IsStrongPairedWall(other)
                || !AreNearParallel(wall.CenterLine, other.CenterLine)
                || other.DrawingLength < Math.Max(wall.DrawingLength * 0.65, context.Options.MinWallLength))
            {
                continue;
            }

            if (LooksLikeDuplicateWallFaceLine(wall.CenterLine, other.PairEvidence.FirstFaceLine, collinearityTolerance)
                || LooksLikeDuplicateWallFaceLine(wall.CenterLine, other.PairEvidence.SecondFaceLine, collinearityTolerance))
            {
                evidence = $"wall evidence: duplicate wall-face line already represented by stronger paired wall body {other.Id}; keep for review but block exact placement";
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeDuplicateWallFaceLine(
        PlanLineSegment candidate,
        PlanLineSegment faceLine,
        double collinearityTolerance)
    {
        if (!AreNearParallel(candidate, faceLine)
            || !AreNearCollinear(candidate, faceLine, collinearityTolerance))
        {
            return false;
        }

        var overlapRatio = AxisAlignedOverlapRatio(candidate, faceLine);
        if (overlapRatio >= 0.52)
        {
            return true;
        }

        var lengthRatio = Math.Min(candidate.Length, faceLine.Length)
            / Math.Max(1, Math.Max(candidate.Length, faceLine.Length));
        return overlapRatio >= 0.38 && lengthRatio <= 0.45;
    }

    private static bool LooksLikeRepeatedShortDetailNeighbor(
        PlanLineSegment first,
        PlanLineSegment second,
        ScannerOptions options)
    {
        if (!AreNearParallel(first, second))
        {
            return false;
        }

        if (AxisAlignedOverlapRatio(first, second) < 0.68)
        {
            return false;
        }

        var spacingLimit = Math.Max(options.MinWallLength * 6.0, options.DefaultWallThickness * 30.0);
        if (first.IsVertical() && second.IsVertical())
        {
            return Math.Abs(first.Midpoint.X - second.Midpoint.X) <= spacingLimit;
        }

        if (first.IsHorizontal() && second.IsHorizontal())
        {
            return Math.Abs(first.Midpoint.Y - second.Midpoint.Y) <= spacingLimit;
        }

        return false;
    }

    private static bool TryClassifySurfacePatternNoise(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.DetectionKind == WallDetectionKind.ParallelLinePair)
        {
            return false;
        }

        if (TryClassifySurfacePatternLayerNoise(wall, context, out evidence))
        {
            return true;
        }

        if (context.SurfacePatterns.Count == 0)
        {
            return false;
        }

        foreach (var pattern in context.SurfacePatterns.Where(pattern => pattern.PageNumber == wall.PageNumber))
        {
            var sharesSource = wall.SourcePrimitiveIds.Any(pattern.SourcePrimitiveIds.Contains);
            if (!pattern.ExcludedFromWallDetection && !pattern.ExcludedFromStructuralTopology)
            {
                continue;
            }

            if (sharesSource)
            {
                evidence = $"wall evidence: rejected as surface/detail pattern because it shares source primitives with {pattern.Id}";
                return true;
            }

            if (IsSurfacePatternOverlapNoise(wall, pattern, context))
            {
                evidence = $"wall evidence: rejected as surface/detail pattern because it sits inside {pattern.Id}";
                return true;
            }
        }

        return false;
    }

    private static bool TryClassifySurfacePatternLayerNoise(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (IsWallLayerBacked(wall, context))
        {
            return false;
        }

        var categories = SourceLayerCategories(wall, context).Distinct().ToArray();
        if (!categories.Any(IsSurfacePatternLayerCategory))
        {
            return false;
        }

        evidence = "wall evidence: rejected as hatch/surface-pattern linework from SurfacePattern layer category";
        return true;
    }

    private static bool TryClassifyPairedSurfacePatternLayerNoise(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is null
            || IsWallLayerBacked(wall, context))
        {
            return false;
        }

        var categories = SourceLayerCategories(wall, context).Distinct().ToArray();
        if (!categories.Any(IsSurfacePatternLayerCategory))
        {
            return false;
        }

        var structuralSupportWallCount = CountDistinctStructuralSupportWalls(
            wall.CenterLine,
            wall.PageNumber,
            context.WallCandidates,
            context.Options);
        if (structuralSupportWallCount >= 2)
        {
            return false;
        }

        evidence = "wall evidence: rejected as paired hatch/surface-pattern linework from SurfacePattern layer category before strong-wall acceptance";
        return true;
    }

    private static bool TryClassifyPairedObjectOrFixtureLineworkNoise(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is null
            || IsWallLayerBacked(wall, context))
        {
            return false;
        }

        var objectCategories = SourceLayerCategories(wall, context)
            .Distinct()
            .Where(IsObjectOrFixtureLayerCategory)
            .ToArray();
        if (objectCategories.Length == 0 || HasTwoSidedStructuralSupport(wall, context))
        {
            return false;
        }

        evidence = $"wall evidence: rejected as paired object/fixture/service linework from {string.Join("/", objectCategories)} layer category before strong-wall acceptance";
        return true;
    }

    private static bool TryClassifyPairedDimensionOrAnnotationNoise(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is null
            || IsWallLayerBacked(wall, context))
        {
            return false;
        }

        var categories = SourceLayerCategories(wall, context)
            .Distinct()
            .ToArray();
        if (!categories.Any(IsAnnotationLayerCategory) || HasTwoSidedStructuralSupport(wall, context))
        {
            return false;
        }

        evidence = "wall evidence: rejected as paired dimension, text, or grid layer linework before strong-wall acceptance";
        return true;
    }

    private static bool HasTwoSidedStructuralSupport(WallSegment wall, ScanContext context) =>
        CountDistinctStructuralSupportWalls(
            wall.CenterLine,
            wall.PageNumber,
            context.WallCandidates,
            context.Options) >= 2;

    private static bool TryClassifyOutdoorAreaBoundaryNoise(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.WallType != WallType.Exterior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is null
            || IsWallLayerBacked(wall, context)
            || !HasLocalBoundaryEvidence(wall))
        {
            return false;
        }

        var pair = wall.PairEvidence;
        var maxDetailSeparation = MaxOutdoorBoundaryDetailSeparation(context.Options);
        if (pair.FaceSeparation > maxDetailSeparation)
        {
            return false;
        }

        var totalFaceFragments = pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount;
        var detailLikeFragmentation = totalFaceFragments >= 6 || wall.SourcePrimitiveIds.Count >= 8;
        if (!detailLikeFragmentation)
        {
            return false;
        }

        var page = context.Document.Pages.FirstOrDefault(page => page.Number == wall.PageNumber);
        if (page is null)
        {
            return false;
        }

        foreach (var text in page.Text)
        {
            var keyword = OutdoorAreaKeyword(text.Text);
            if (keyword is null)
            {
                continue;
            }

            if (!OutdoorLabelMatchesWallSpan(text.Bounds, wall, context.Options))
            {
                continue;
            }

            evidence = $"wall evidence: rejected as outdoor covered-area boundary near '{keyword}' label; local outer-boundary evidence is not enough for structural exterior wall placement";
            return true;
        }

        return false;
    }

    private static bool TryClassifyOutdoorAreaBoundaryReview(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.WallType != WallType.Exterior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is null
            || IsWallLayerBacked(wall, context)
            || !HasLocalBoundaryEvidence(wall))
        {
            return false;
        }

        var pair = wall.PairEvidence;
        var maxDetailSeparation = MaxOutdoorBoundaryDetailSeparation(context.Options);
        if (pair.FaceSeparation > maxDetailSeparation)
        {
            return false;
        }

        var structuralSupportWallCount = CountDistinctStructuralSupportWalls(
            wall.CenterLine,
            wall.PageNumber,
            context.WallCandidates,
            context.Options);
        if (structuralSupportWallCount >= 2)
        {
            return false;
        }

        var page = context.Document.Pages.FirstOrDefault(page => page.Number == wall.PageNumber);
        if (page is null)
        {
            return false;
        }

        foreach (var text in page.Text)
        {
            var keyword = OutdoorAreaKeyword(text.Text);
            if (keyword is null || !OutdoorLabelMatchesWallSpan(text.Bounds, wall, context.Options))
            {
                continue;
            }

            evidence = structuralSupportWallCount == 0
                ? $"wall evidence: outdoor covered-area boundary near '{keyword}' is review-only; thin unlayered local-boundary pair has no distinct structural support for placement"
                : $"wall evidence: outdoor covered-area boundary near '{keyword}' is review-only; thin unlayered local-boundary pair is supported by only one distinct structural wall";
            return true;
        }

        return false;
    }

    private static bool TryClassifyUnpairedOutdoorAreaBoundaryReview(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.WallType != WallType.Exterior
            || wall.PairEvidence is not null
            || IsWallLayerBacked(wall, context)
            || !HasLocalBoundaryEvidence(wall)
            || wall.DetectionKind is not (WallDetectionKind.SingleLine or WallDetectionKind.FragmentMerged))
        {
            return false;
        }

        var structuralSupportWallCount = CountDistinctStructuralSupportWalls(
            wall.CenterLine,
            wall.PageNumber,
            context.WallCandidates,
            context.Options);
        if (structuralSupportWallCount >= 2)
        {
            return false;
        }

        var page = context.Document.Pages.FirstOrDefault(page => page.Number == wall.PageNumber);
        if (page is null)
        {
            return false;
        }

        foreach (var text in page.Text)
        {
            var keyword = OutdoorAreaKeyword(text.Text);
            if (keyword is null || !OutdoorLabelMatchesWallSpan(text.Bounds, wall, context.Options))
            {
                continue;
            }

            evidence = structuralSupportWallCount == 0
                ? $"wall evidence: unpaired outdoor covered-area boundary near '{keyword}' is review-only; single-line local-boundary evidence has no distinct structural support for placement"
                : $"wall evidence: unpaired outdoor covered-area boundary near '{keyword}' is review-only; single-line local-boundary evidence is supported by only one distinct structural wall";
            return true;
        }

        return false;
    }

    private static bool HasLocalBoundaryEvidence(WallSegment wall) =>
        wall.Evidence.Any(item =>
            item.Contains("local outer boundary", StringComparison.OrdinalIgnoreCase)
            || item.Contains("floorplan/wall envelope", StringComparison.OrdinalIgnoreCase));

    private static double MaxOutdoorBoundaryDetailSeparation(ScannerOptions options) =>
        Math.Max(options.DefaultWallThickness * 3.5, options.WallSnapTolerance * 5.0);

    private static PlanRect OutdoorLabelInfluenceBounds(PlanRect bounds, ScannerOptions options)
    {
        var xPadding = Math.Max(options.MinWallLength * 4.0, options.DefaultWallThickness * 22.0);
        var yPadding = Math.Max(options.MinWallLength * 1.8, options.DefaultWallThickness * 12.0);
        return new PlanRect(
            bounds.X - xPadding,
            bounds.Y - yPadding,
            bounds.Width + (xPadding * 2.0),
            bounds.Height + (yPadding * 2.0));
    }

    private static bool OutdoorLabelMatchesWallSpan(
        PlanRect textBounds,
        WallSegment wall,
        ScannerOptions options)
    {
        var labelBounds = OutdoorLabelInfluenceBounds(textBounds, options);
        var tolerance = options.WallSnapTolerance;
        if (!labelBounds.Intersects(wall.Bounds, tolerance))
        {
            return false;
        }

        if (labelBounds.Contains(wall.CenterLine.Midpoint, tolerance))
        {
            return true;
        }

        var samplesInside = 0;
        const int sampleCount = 5;
        for (var index = 0; index < sampleCount; index++)
        {
            var t = index / (double)(sampleCount - 1);
            if (labelBounds.Contains(wall.CenterLine.PointAt(t), tolerance))
            {
                samplesInside++;
            }
        }

        return samplesInside >= 2;
    }

    private static string? OutdoorAreaKeyword(string value)
    {
        var normalized = NormalizeSearchText(value);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        foreach (var keyword in OutdoorAreaKeywords)
        {
            if (normalized.Contains(keyword, StringComparison.Ordinal))
            {
                return keyword;
            }
        }

        return null;
    }

    private static string NormalizeSearchText(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[length++] = char.ToLowerInvariant(character);
            }
        }

        return new string(buffer[..length]);
    }

    private static readonly string[] OutdoorAreaKeywords =
    {
        "overbygd",
        "covered",
        "canopy",
        "porch",
        "carport",
        "terrasse",
        "terrace",
        "balkong",
        "balcony",
        "veranda",
        "uteplass",
        "patio"
    };

    private static bool IsSurfacePatternOverlapNoise(
        WallSegment wall,
        SurfacePatternCandidate pattern,
        ScanContext context)
    {
        if (IsWallLayerBacked(wall, context)
            || wall.DrawingLength < Math.Max(context.Options.MinWallLength * 0.75, 12))
        {
            return false;
        }

        var orientation = ResolveAxisOrientation(wall.CenterLine);
        if (orientation == WallOrientation.Unknown)
        {
            return false;
        }

        if (pattern.Kind == SurfacePatternKind.DenseParallelBand
            && pattern.Orientation is SurfacePatternOrientation.Horizontal or SurfacePatternOrientation.Vertical
            && !PatternOrientationMatchesWall(pattern.Orientation, orientation))
        {
            return false;
        }

        var tolerance = Math.Max(context.Options.WallSnapTolerance * 2.0, context.Options.DefaultWallThickness);
        var patternBounds = pattern.Bounds.Inflate(tolerance);
        if (!patternBounds.Contains(wall.CenterLine.Midpoint, tolerance)
            || !patternBounds.Intersects(wall.Bounds, tolerance))
        {
            return false;
        }

        var insideSamples = 0;
        const int sampleCount = 5;
        for (var index = 0; index < sampleCount; index++)
        {
            var t = sampleCount == 1 ? 0.5 : index / (double)(sampleCount - 1);
            if (patternBounds.Contains(wall.CenterLine.PointAt(t), tolerance))
            {
                insideSamples++;
            }
        }

        if (insideSamples < 4)
        {
            return false;
        }

        var longSide = Math.Max(pattern.Bounds.Width, pattern.Bounds.Height);
        var maxPatternDetailLength = Math.Max(context.Options.MinWallLength * 1.25, longSide * 1.15);
        return wall.DrawingLength <= maxPatternDetailLength;
    }

    private static bool PatternOrientationMatchesWall(
        SurfacePatternOrientation patternOrientation,
        WallOrientation wallOrientation) =>
        patternOrientation == SurfacePatternOrientation.Horizontal && wallOrientation == WallOrientation.Horizontal
        || patternOrientation == SurfacePatternOrientation.Vertical && wallOrientation == WallOrientation.Vertical;

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
        var radialLeafSupport = NearbyRadialDoorLeafArcSupport(wall, page, context.Options);
        var lengthLimit = Math.Max(context.Options.MaxOpeningGap * 1.7, context.Options.MinWallLength * 2.0);
        var structuralEndpointSupportCount = CountStructuralEndpointSupport(
            wall.CenterLine,
            wall.PageNumber,
            context.WallCandidates,
            context.Options);
        var structuralSupportWallCount = CountDistinctStructuralSupportWalls(
            wall.CenterLine,
            wall.PageNumber,
            context.WallCandidates,
            context.Options);

        if (radialLeafSupport.Score >= 0.76
            && wall.DrawingLength <= lengthLimit
            && !HasOpeningFragmentCompanion(wall, context.WallCandidates, context.Options))
        {
            evidence = doorLayerBacked
                ? $"wall evidence: rejected as door/opening leaf linework from door/window layer and radial swing arc {radialLeafSupport.ArcSourceId}"
                : $"wall evidence: rejected as door/opening leaf linework radially tied to swing arc {radialLeafSupport.ArcSourceId}";
            return true;
        }

        if (doorLayerBacked
            && !IsWallLayerBacked(wall, context)
            && arcSupport.Score >= 0.50
            && wall.DrawingLength <= lengthLimit
            && !HasOpeningFragmentCompanion(wall, context.WallCandidates, context.Options))
        {
            evidence = structuralEndpointSupportCount > 0
                ? $"wall evidence: rejected as door/window layer linework tied to swing arc {arcSupport.ArcSourceId} despite structural endpoint support"
                : $"wall evidence: rejected as door/window layer linework tied to swing arc {arcSupport.ArcSourceId}";
            return true;
        }

        if (!doorLayerBacked
            && !IsWallLayerBacked(wall, context)
            && arcSupport.Score >= 0.72
            && structuralSupportWallCount <= 1
            && wall.DrawingLength <= Math.Max(context.Options.MaxOpeningGap * 1.25, context.Options.MinWallLength * 1.6)
            && !HasOpeningFragmentCompanion(wall, context.WallCandidates, context.Options))
        {
            evidence = structuralSupportWallCount > 0
                ? $"wall evidence: rejected as unlayered door/opening symbol linework strongly tied to swing arc {arcSupport.ArcSourceId} with only one structural support wall"
                : $"wall evidence: rejected as unlayered door/opening symbol linework strongly tied to swing arc {arcSupport.ArcSourceId}";
            return true;
        }

        if ((doorLayerBacked || arcSupport.Score >= 0.68)
            && wall.DrawingLength <= lengthLimit
            && !HasOpeningFragmentCompanion(wall, context.WallCandidates, context.Options)
            && structuralEndpointSupportCount == 0)
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

    private static bool TryClassifyPairedDoorOrOpeningSymbolNoise(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is null
            || IsWallLayerBacked(wall, context))
        {
            return false;
        }

        var page = context.Document.Pages.FirstOrDefault(page => page.Number == wall.PageNumber);
        if (page is null)
        {
            return false;
        }

        var lengthLimit = Math.Max(context.Options.MaxOpeningGap * 1.7, context.Options.MinWallLength * 2.0);
        var separationLimit = Math.Max(context.Options.DefaultWallThickness * 2.0, context.Options.WallSnapTolerance * 4.0);
        if (wall.DrawingLength > lengthLimit || wall.PairEvidence.FaceSeparation > separationLimit)
        {
            return false;
        }

        var structuralSupportWallCount = CountDistinctStructuralSupportWalls(
            wall.CenterLine,
            wall.PageNumber,
            context.WallCandidates,
            context.Options);
        if (structuralSupportWallCount >= 2)
        {
            return false;
        }

        var layerCategories = SourceLayerCategories(wall, context).Distinct().ToArray();
        var doorLayerBacked = layerCategories.Any(category => category is LayerCategory.Door or LayerCategory.Window);
        var arcSupport = NearbyDoorArcSupport(wall, page, context.Options);
        var hasSwingArcEvidence = arcSupport.Score >= (doorLayerBacked ? 0.42 : 0.62);
        if (!doorLayerBacked && !hasSwingArcEvidence)
        {
            return false;
        }

        if (!hasSwingArcEvidence && structuralSupportWallCount > 0)
        {
            return false;
        }

        evidence = doorLayerBacked
            ? hasSwingArcEvidence
                ? $"wall evidence: rejected as paired door/window frame linework from door/window layer near swing arc {arcSupport.ArcSourceId}"
                : "wall evidence: rejected as unsupported paired door/window frame linework from door/window layer"
            : $"wall evidence: rejected as unlayered paired door/window frame linework strongly tied to swing arc {arcSupport.ArcSourceId}";
        return true;
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

    private static bool IsAnnotationLayerCategory(LayerCategory category) =>
        category is LayerCategory.Dimension or LayerCategory.Text or LayerCategory.Grid;

    private static bool TryClassifyObjectOrFixtureLineworkNoise(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.DetectionKind == WallDetectionKind.ParallelLinePair
            || IsWallLayerBacked(wall, context))
        {
            return false;
        }

        var categories = SourceLayerCategories(wall, context).Distinct().ToArray();
        var objectCategories = categories
            .Where(IsObjectOrFixtureLayerCategory)
            .ToArray();
        if (objectCategories.Length == 0)
        {
            return false;
        }

        var structuralEndpointSupportCount = CountStructuralEndpointSupport(
            wall.CenterLine,
            wall.PageNumber,
            context.WallCandidates,
            context.Options);
        var structuralSupportWallCount = CountDistinctStructuralSupportWalls(
            wall.CenterLine,
            wall.PageNumber,
            context.WallCandidates,
            context.Options);
        if (structuralEndpointSupportCount >= 2 || structuralSupportWallCount >= 2)
        {
            return false;
        }

        evidence = $"wall evidence: rejected as object/fixture/service linework from {string.Join("/", objectCategories)} layer category";
        return true;
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

    private static bool TryClassifyDimensionGeometryNoise(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.DetectionKind == WallDetectionKind.ParallelLinePair
            || wall.WallType == WallType.Exterior
            || IsWallLayerBacked(wall, context))
        {
            return false;
        }

        var orientation = ResolveAxisOrientation(wall.CenterLine);
        if (orientation == WallOrientation.Unknown)
        {
            return false;
        }

        var maxDistance = Math.Max(context.Options.DefaultWallThickness * 4.0, context.Options.WallSnapTolerance * 5.0);
        foreach (var dimension in context.Dimensions.Where(dimension => dimension.PageNumber == wall.PageNumber))
        {
            if (dimension.DimensionLine is not { } dimensionLine
                || !DimensionOrientationMatches(dimension.Orientation, orientation)
                || ResolveAxisOrientation(dimensionLine) != orientation)
            {
                continue;
            }

            var distance = AxisPerpendicularDistance(wall.CenterLine, dimensionLine, orientation);
            if (distance > maxDistance)
            {
                continue;
            }

            var overlapRatio = AxisAlignedOverlapRatio(wall.CenterLine, dimensionLine);
            if (overlapRatio < 0.72)
            {
                continue;
            }

            var lengthRatio = Math.Min(wall.CenterLine.Length, dimensionLine.Length)
                / Math.Max(1, Math.Max(wall.CenterLine.Length, dimensionLine.Length));
            if (lengthRatio < 0.70)
            {
                continue;
            }

            evidence = "wall evidence: rejected as unlayered wall candidate aligned with detected dimension "
                + $"'{dimension.Text}' ({Math.Round(distance, 2).ToString(CultureInfo.InvariantCulture)} drawing units away, "
                + $"{Math.Round(overlapRatio, 2).ToString(CultureInfo.InvariantCulture)} overlap)";
            return true;
        }

        return false;
    }

    private static bool DimensionOrientationMatches(DimensionOrientation dimensionOrientation, WallOrientation wallOrientation) =>
        (dimensionOrientation == DimensionOrientation.Horizontal && wallOrientation == WallOrientation.Horizontal)
        || (dimensionOrientation == DimensionOrientation.Vertical && wallOrientation == WallOrientation.Vertical);

    private static double AxisPerpendicularDistance(
        PlanLineSegment first,
        PlanLineSegment second,
        WallOrientation orientation) =>
        orientation == WallOrientation.Horizontal
            ? Math.Abs(first.Midpoint.Y - second.Midpoint.Y)
            : Math.Abs(first.Midpoint.X - second.Midpoint.X);

    private static IReadOnlyList<WallSegment> RecoverMissingWalls(
        ScanContext context,
        CancellationToken cancellationToken)
    {
        var recoveredBands = RecoverMissingWallBands(context, cancellationToken);
        var recoveredShortWalls = RecoverShortSupportedWallSegments(
            context,
            context.WallCandidates.Concat(recoveredBands).ToArray(),
            cancellationToken);
        return recoveredBands
            .Concat(recoveredShortWalls)
            .ToArray();
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

    private static IReadOnlyList<WallSegment> RecoverShortSupportedWallSegments(
        ScanContext context,
        IReadOnlyList<WallSegment> existingWalls,
        CancellationToken cancellationToken)
    {
        var recovered = new List<WallSegment>();
        var surfaceSourceIds = context.SurfacePatterns
            .Where(pattern => pattern.ExcludedFromWallDetection || pattern.ExcludedFromStructuralTopology)
            .SelectMany(pattern => pattern.SourcePrimitiveIds)
            .ToHashSet(StringComparer.Ordinal);
        var usedSourceIds = existingWalls
            .SelectMany(wall => wall.SourcePrimitiveIds)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var page in context.Document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mainRegion = context.SheetRegions
                .Where(region => region.PageNumber == page.Number && region.Kind == RegionKind.MainFloorPlan)
                .OrderByDescending(region => region.Bounds.Area)
                .FirstOrDefault();
            var allowedBounds = mainRegion?.Bounds ?? page.Bounds;
            var pageExistingWalls = existingWalls
                .Concat(recovered)
                .Where(wall => wall.PageNumber == page.Number)
                .ToArray();
            var shortLineCandidates = PageShortLineCandidates(page, allowedBounds, surfaceSourceIds, usedSourceIds, context)
                .ToArray();
            var repeatedShortDetailSourceIds = RepeatedShortDetailSourceIds(
                shortLineCandidates,
                pageExistingWalls,
                context.Options);
            AddRepeatedShortRecoveryNoiseDiagnostic(context, page.Number, repeatedShortDetailSourceIds);
            var sequence = 1;

            foreach (var candidate in shortLineCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (repeatedShortDetailSourceIds.Contains(candidate.SourceId)
                    || IsRepresentedByExistingWall(candidate.Segment, pageExistingWalls, context.Options)
                    || IsRepresentedByExistingWall(candidate.Segment, recovered, context.Options)
                    || IsSurfacePatternRecoveryNoise(candidate.Segment, page.Number, context))
                {
                    continue;
                }

                var structuralSupportCount = CountStructuralEndpointSupport(
                    candidate.Segment,
                    page.Number,
                    pageExistingWalls,
                    context.Options);
                var wallLayerBacked = IsWallLikeCategory(candidate.LayerCategory);
                var minimumLayerBackedLength = Math.Max(
                    context.Options.MinWallLength * 0.50,
                    context.Options.DefaultWallThickness * 3.0);
                var minimumUnlayeredLength = Math.Max(
                    context.Options.MinWallLength * 0.60,
                    context.Options.DefaultWallThickness * 4.0);
                if (wallLayerBacked)
                {
                    if (structuralSupportCount < 1 || candidate.Length < minimumLayerBackedLength)
                    {
                        continue;
                    }
                }
                else if (structuralSupportCount < 2 || candidate.Length < minimumUnlayeredLength)
                {
                    continue;
                }

                var score = Math.Clamp(
                    0.54
                    + (wallLayerBacked ? 0.10 : 0)
                    + (structuralSupportCount * 0.08)
                    + Math.Min(0.08, candidate.Length / Math.Max(context.Options.MinWallLength * 8.0, 1)),
                    0,
                    0.86);
                recovered.Add(CreateRecoveredShortWall(
                    page.Number,
                    mainRegion?.Id,
                    candidate,
                    structuralSupportCount,
                    score,
                    context,
                    sequence++));
                usedSourceIds.Add(candidate.SourceId);
            }
        }

        return recovered;
    }

    private static IReadOnlySet<string> RepeatedShortDetailSourceIds(
        IReadOnlyList<PrimitiveLineCandidate> candidates,
        IReadOnlyList<WallSegment> existingWalls,
        ScannerOptions options)
    {
        var suppressed = new HashSet<string>(StringComparer.Ordinal);
        var coordinateTolerance = Math.Max(options.DefaultWallThickness * 8.0, options.WallSnapTolerance * 8.0);
        var minimumCoordinateSpan = Math.Max(options.DefaultWallThickness * 3.0, options.WallSnapTolerance * 4.0);
        var collinearCoordinateTolerance = Math.Max(options.DefaultWallThickness * 1.5, options.WallSnapTolerance * 2.0);
        var minimumAlongSpan = Math.Max(options.DefaultWallThickness * 12.0, options.MinWallLength * 2.0);

        foreach (var group in candidates
            .Where(candidate => !IsWallLikeCategory(candidate.LayerCategory))
            .Where(candidate => CountStructuralEndpointSupport(candidate.Segment, candidate.PageNumber, existingWalls, options) >= 2)
            .GroupBy(candidate => candidate.Orientation))
        {
            if (group.Key == WallOrientation.Unknown)
            {
                continue;
            }

            var ordered = group
                .OrderBy(candidate => candidate.Coordinate)
                .ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var seed = ordered[index];
                var cluster = new List<PrimitiveLineCandidate> { seed };
                for (var otherIndex = index - 1; otherIndex >= 0; otherIndex--)
                {
                    var other = ordered[otherIndex];
                    if (seed.Coordinate - other.Coordinate > coordinateTolerance)
                    {
                        break;
                    }

                    if (IsRepeatedShortDetailNeighbor(seed, other, options))
                    {
                        cluster.Add(other);
                    }
                }

                for (var otherIndex = index + 1; otherIndex < ordered.Length; otherIndex++)
                {
                    var other = ordered[otherIndex];
                    if (other.Coordinate - seed.Coordinate > coordinateTolerance)
                    {
                        break;
                    }

                    if (IsRepeatedShortDetailNeighbor(seed, other, options))
                    {
                        cluster.Add(other);
                    }
                }

                if (cluster.Count < 3 || ShortCandidateCoordinateSpan(cluster) < minimumCoordinateSpan)
                {
                    continue;
                }

                foreach (var candidate in cluster)
                {
                    suppressed.Add(candidate.SourceId);
                }
            }

            var coordinateBuckets = ordered
                .GroupBy(candidate => (int)Math.Round(candidate.Coordinate / Math.Max(collinearCoordinateTolerance, 0.001)))
                .Select(bucket => bucket.OrderBy(candidate => candidate.MinAlong).ToArray());
            foreach (var bucket in coordinateBuckets)
            {
                for (var index = 0; index < bucket.Length; index++)
                {
                    var seed = bucket[index];
                    var cluster = new List<PrimitiveLineCandidate> { seed };
                    for (var otherIndex = 0; otherIndex < bucket.Length; otherIndex++)
                    {
                        if (otherIndex == index)
                        {
                            continue;
                        }

                        var other = bucket[otherIndex];
                        if (Math.Abs(other.Coordinate - seed.Coordinate) <= collinearCoordinateTolerance
                            && IsRepeatedCollinearShortDetailNeighbor(seed, other, options))
                        {
                            cluster.Add(other);
                        }
                    }

                    if (cluster.Count < 3 || ShortCandidateAlongSpan(cluster) < minimumAlongSpan)
                    {
                        continue;
                    }

                    foreach (var candidate in cluster)
                    {
                        suppressed.Add(candidate.SourceId);
                    }
                }
            }
        }

        return suppressed;
    }

    private static bool IsRepeatedShortDetailNeighbor(
        PrimitiveLineCandidate first,
        PrimitiveLineCandidate second,
        ScannerOptions options)
    {
        var alongEndpointTolerance = Math.Max(options.DefaultWallThickness * 1.5, options.WallSnapTolerance * 2.0);
        if (Math.Abs(first.MinAlong - second.MinAlong) > alongEndpointTolerance
            || Math.Abs(first.MaxAlong - second.MaxAlong) > alongEndpointTolerance)
        {
            return false;
        }

        var overlap = AxisOverlap(first, second);
        var overlapRatio = overlap.Length / Math.Max(1, Math.Min(first.Length, second.Length));
        if (overlapRatio < 0.70)
        {
            return false;
        }

        var lengthRatio = Math.Min(first.Length, second.Length) / Math.Max(1, Math.Max(first.Length, second.Length));
        return lengthRatio >= 0.70;
    }

    private static bool IsRepeatedCollinearShortDetailNeighbor(
        PrimitiveLineCandidate first,
        PrimitiveLineCandidate second,
        ScannerOptions options)
    {
        var lengthRatio = Math.Min(first.Length, second.Length) / Math.Max(1, Math.Max(first.Length, second.Length));
        if (lengthRatio < 0.70)
        {
            return false;
        }

        var gap = Math.Max(first.MinAlong, second.MinAlong) - Math.Min(first.MaxAlong, second.MaxAlong);
        var minimumGap = Math.Max(
            options.DefaultWallThickness * 2.0,
            Math.Min(first.Length, second.Length) * 0.45);
        var maximumGap = Math.Max(options.MinWallLength * 5.0, options.DefaultWallThickness * 36.0);
        return gap >= minimumGap && gap <= maximumGap;
    }

    private static double ShortCandidateCoordinateSpan(IReadOnlyList<PrimitiveLineCandidate> candidates)
    {
        var coordinates = candidates.Select(candidate => candidate.Coordinate).ToArray();
        return coordinates.Length == 0 ? 0 : coordinates.Max() - coordinates.Min();
    }

    private static double ShortCandidateAlongSpan(IReadOnlyList<PrimitiveLineCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return 0;
        }

        return candidates.Max(candidate => candidate.MaxAlong) - candidates.Min(candidate => candidate.MinAlong);
    }

    private static void AddRepeatedShortRecoveryNoiseDiagnostic(
        ScanContext context,
        int pageNumber,
        IReadOnlySet<string> suppressedSourceIds)
    {
        if (suppressedSourceIds.Count == 0)
        {
            return;
        }

        context.AddDiagnostic(
            "wall_evidence.short_repeated_slots_suppressed",
            DiagnosticSeverity.Info,
            StageName,
            "Repeated short supported linework was suppressed from recovered walls as likely fixture, shelf, closet, or detail slots.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: suppressedSourceIds,
            properties: new Dictionary<string, string>
            {
                ["pageNumber"] = pageNumber.ToString(CultureInfo.InvariantCulture),
                ["suppressedSourceCount"] = suppressedSourceIds.Count.ToString(CultureInfo.InvariantCulture),
                ["sampleSourceIds"] = string.Join(",", suppressedSourceIds.Order(StringComparer.Ordinal).Take(12))
            });
    }

    private static IEnumerable<PrimitiveLineCandidate> PageShortLineCandidates(
        PlanPage page,
        PlanRect allowedBounds,
        IReadOnlySet<string> surfaceSourceIds,
        IReadOnlySet<string> usedSourceIds,
        ScanContext context)
    {
        var minimumLength = Math.Max(
            context.Options.MinWallFragmentLength * 1.5,
            context.Options.DefaultWallThickness * 2.5);
        var maximumLength = Math.Max(context.Options.MinWallLength * 1.15, minimumLength + 1);

        for (var index = 0; index < page.Primitives.Count; index++)
        {
            if (page.Primitives[index] is not LinePrimitive line)
            {
                continue;
            }

            var sourceId = context.PrimitiveId(page.Number, index, line);
            if (usedSourceIds.Contains(sourceId)
                || surfaceSourceIds.Contains(sourceId)
                || line.Segment.Length < minimumLength
                || line.Segment.Length >= maximumLength
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
                or LayerCategory.FireSafety
                or LayerCategory.SurfacePattern)
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
                or LayerCategory.FireSafety
                or LayerCategory.SurfacePattern)
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
                    var structuralSupportCount = CountStructuralEndpointSupport(centerLine, pageNumber, existingWalls, context.Options);
                    var hasStructuralSupport = structuralSupportCount > 0;
                    if (!wallLayerBacked && structuralSupportCount < 2)
                    {
                        continue;
                    }

                    if (!wallLayerBacked && IsSurfacePatternRecoveryNoise(centerLine, pageNumber, context))
                    {
                        continue;
                    }

                    var score = Math.Clamp(
                        0.44
                        + (overlapRatio * 0.22)
                        + (wallLayerBacked ? 0.18 : 0)
                        + (structuralSupportCount * 0.08)
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

        var denseParallelRecoveryNoise = DenseParallelRecoveryNoiseKeys(pairCandidates, context.Options);
        foreach (var pair in pairCandidates
            .OrderByDescending(pair => pair.Score)
            .ThenByDescending(pair => pair.CenterLine.Length))
        {
            if (consumed.Contains(pair.First.SourceId) || consumed.Contains(pair.Second.SourceId))
            {
                continue;
            }

            if (denseParallelRecoveryNoise.Contains(RecoveredPairKey(pair)))
            {
                continue;
            }

            if (IsRepresentedByExistingWall(pair.CenterLine, existingWalls.Concat(recovered).ToArray(), context.Options))
            {
                continue;
            }

            consumed.Add(pair.First.SourceId);
            consumed.Add(pair.Second.SourceId);
            recovered.Add(CreateRecoveredWall(pageNumber, sourceRegionId, pair, context, recovered.Count + 1));
        }

        return recovered;
    }

    private static IReadOnlySet<string> DenseParallelRecoveryNoiseKeys(
        IReadOnlyList<RecoveredPairCandidate> pairs,
        ScannerOptions options)
    {
        var noise = new HashSet<string>(StringComparer.Ordinal);
        var coordinateTolerance = Math.Max(options.DefaultWallThickness * 4.0, options.WallSnapTolerance * 6.0);
        foreach (var group in pairs
            .Where(pair => !IsRecoveredPairWallLayerBacked(pair))
            .GroupBy(pair => ResolveAxisOrientation(pair.CenterLine)))
        {
            if (group.Key == WallOrientation.Unknown)
            {
                continue;
            }

            var ordered = group
                .OrderBy(pair => AxisCoordinate(pair.CenterLine, group.Key))
                .ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var seed = ordered[index];
                var seedCoordinate = AxisCoordinate(seed.CenterLine, group.Key);
                var cluster = new List<RecoveredPairCandidate> { seed };
                for (var otherIndex = index - 1; otherIndex >= 0; otherIndex--)
                {
                    var other = ordered[otherIndex];
                    if (seedCoordinate - AxisCoordinate(other.CenterLine, group.Key) > coordinateTolerance)
                    {
                        break;
                    }

                    if (IsDenseParallelRecoveryNeighbor(seed, other))
                    {
                        cluster.Add(other);
                    }
                }

                for (var otherIndex = index + 1; otherIndex < ordered.Length; otherIndex++)
                {
                    var other = ordered[otherIndex];
                    if (AxisCoordinate(other.CenterLine, group.Key) - seedCoordinate > coordinateTolerance)
                    {
                        break;
                    }

                    if (IsDenseParallelRecoveryNeighbor(seed, other))
                    {
                        cluster.Add(other);
                    }
                }

                if (cluster.Count < 4
                    || AxisCoordinateSpan(cluster, group.Key) < Math.Max(options.DefaultWallThickness * 2.0, options.WallSnapTolerance * 4.0))
                {
                    continue;
                }

                foreach (var pair in cluster)
                {
                    noise.Add(RecoveredPairKey(pair));
                }
            }
        }

        return noise;
    }

    private static bool IsDenseParallelRecoveryNeighbor(
        RecoveredPairCandidate first,
        RecoveredPairCandidate second)
    {
        var overlapRatio = AxisAlignedOverlapRatio(first.CenterLine, second.CenterLine);
        if (overlapRatio < 0.65)
        {
            return false;
        }

        var lengthRatio = Math.Min(first.CenterLine.Length, second.CenterLine.Length)
            / Math.Max(1, Math.Max(first.CenterLine.Length, second.CenterLine.Length));
        return lengthRatio >= 0.60;
    }

    private static double AxisCoordinateSpan(
        IReadOnlyList<RecoveredPairCandidate> pairs,
        WallOrientation orientation)
    {
        var coordinates = pairs.Select(pair => AxisCoordinate(pair.CenterLine, orientation)).ToArray();
        return coordinates.Length == 0 ? 0 : coordinates.Max() - coordinates.Min();
    }

    private static bool IsRecoveredPairWallLayerBacked(RecoveredPairCandidate pair) =>
        IsWallLikeCategory(pair.First.LayerCategory) || IsWallLikeCategory(pair.Second.LayerCategory);

    private static double AxisCoordinate(PlanLineSegment line, WallOrientation orientation) =>
        orientation == WallOrientation.Horizontal
            ? line.Midpoint.Y
            : line.Midpoint.X;

    private static string RecoveredPairKey(RecoveredPairCandidate pair) =>
        string.Compare(pair.First.SourceId, pair.Second.SourceId, StringComparison.Ordinal) <= 0
            ? $"{pair.First.SourceId}\u001f{pair.Second.SourceId}"
            : $"{pair.Second.SourceId}\u001f{pair.First.SourceId}";

    private static bool IsSurfacePatternRecoveryNoise(
        PlanLineSegment centerLine,
        int pageNumber,
        ScanContext context)
    {
        var syntheticWall = new WallSegment(
            "wall-evidence-recovery-candidate",
            pageNumber,
            centerLine,
            context.Options.DefaultWallThickness,
            Confidence.Medium);
        return context.SurfacePatterns
            .Where(pattern => pattern.PageNumber == pageNumber)
            .Any(pattern => (pattern.ExcludedFromWallDetection || pattern.ExcludedFromStructuralTopology)
                && IsSurfacePatternOverlapNoise(syntheticWall, pattern, context));
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

    private static WallSegment CreateRecoveredShortWall(
        int pageNumber,
        string? sourceRegionId,
        PrimitiveLineCandidate candidate,
        int structuralSupportCount,
        double score,
        ScanContext context,
        int sequence)
    {
        var thickness = context.Options.DefaultWallThickness;
        var bounds = candidate.Segment.Bounds.Inflate(Math.Max(thickness / 2.0, 0.5));
        var scaleGroup = context.Calibration.SelectMeasurementScaleGroup(pageNumber, bounds, sourceRegionId);
        return new WallSegment(
            $"page:{pageNumber}:wall-evidence-recovered-short:{sequence:000}",
            pageNumber,
            candidate.Segment,
            thickness,
            new Confidence(score))
        {
            DetectionKind = WallDetectionKind.SingleLine,
            SourceRegionId = sourceRegionId,
            SourcePrimitiveIds = new[] { candidate.SourceId },
            Evidence = new[]
            {
                "recovered by wall evidence map as short supported wall segment",
                $"structural endpoint support count {structuralSupportCount.ToString(CultureInfo.InvariantCulture)}",
                $"source layer category {candidate.LayerCategory}",
                $"recovery score {Math.Round(score, 3).ToString(CultureInfo.InvariantCulture)}"
            },
            LengthMeters = context.Calibration.ToMeters(candidate.Segment.Length, scaleGroup),
            ThicknessMillimeters = context.Calibration.ToMillimeters(thickness, scaleGroup),
            MeasurementScaleGroupId = scaleGroup?.Id
        };
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
        ScannerOptions options) =>
        CountStructuralEndpointSupport(line, pageNumber, walls, options) > 0;

    private static int CountStructuralEndpointSupport(
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

        return supportedEndpoints;
    }

    private static int CountDistinctStructuralSupportWalls(
        PlanLineSegment line,
        int pageNumber,
        IReadOnlyList<WallSegment> walls,
        ScannerOptions options)
    {
        var tolerance = Math.Max(options.WallSnapTolerance * 3.0, options.DefaultWallThickness * 2.5);
        return walls
            .Where(wall => wall.PageNumber == pageNumber)
            .Where(wall => wall.PairEvidence is not null || wall.DrawingLength >= options.MinWallLength * 1.4)
            .Where(wall => !SameLine(line, wall.CenterLine, options.WallSnapTolerance))
            .Where(wall => wall.CenterLine.DistanceToPoint(line.Start) <= tolerance
                || wall.CenterLine.DistanceToPoint(line.End) <= tolerance)
            .Select(wall => wall.Id)
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    private static int CountTrustedStructuralEndpointSupport(
        PlanLineSegment line,
        int pageNumber,
        ScanContext context)
    {
        var tolerance = Math.Max(context.Options.WallSnapTolerance * 3.0, context.Options.DefaultWallThickness * 2.5);
        var supportedEndpoints = 0;
        foreach (var endpoint in new[] { line.Start, line.End })
        {
            if (context.WallCandidates
                .Where(wall => wall.PageNumber == pageNumber)
                .Where(wall => IsTrustedStructuralSupportWall(wall, line, context))
                .Any(wall => wall.CenterLine.DistanceToPoint(endpoint) <= tolerance))
            {
                supportedEndpoints++;
            }
        }

        return supportedEndpoints;
    }

    private static int CountDistinctTrustedStructuralSupportWalls(
        PlanLineSegment line,
        int pageNumber,
        ScanContext context)
    {
        var tolerance = Math.Max(context.Options.WallSnapTolerance * 3.0, context.Options.DefaultWallThickness * 2.5);
        return context.WallCandidates
            .Where(wall => wall.PageNumber == pageNumber)
            .Where(wall => IsTrustedStructuralSupportWall(wall, line, context))
            .Where(wall => wall.CenterLine.DistanceToPoint(line.Start) <= tolerance
                || wall.CenterLine.DistanceToPoint(line.End) <= tolerance)
            .Select(wall => wall.Id)
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    private static bool IsTrustedStructuralSupportWall(
        WallSegment wall,
        PlanLineSegment targetLine,
        ScanContext context) =>
        !SameLine(targetLine, wall.CenterLine, context.Options.WallSnapTolerance)
        && (IsStrongPairedWall(wall) || IsWallLayerBacked(wall, context));

    private static NearbyArcSupport NearbyDoorArcSupport(
        WallSegment wall,
        PlanPage page,
        ScannerOptions options)
    {
        var searchBounds = wall.Bounds.Inflate(Math.Max(options.MaxOpeningGap * 0.75, options.WallSnapTolerance * 8.0));
        var best = NearbyArcSupport.Empty;

        for (var index = 0; index < page.Primitives.Count; index++)
        {
            var primitive = page.Primitives[index];
            if (!TryResolveDoorSwingArcPrimitive(primitive, options, out var arc)
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
                    primitive.SourceId ?? primitive.Source.SourceId ?? $"p{page.Number}:primitive:{index}",
                    arc.Bounds);
            }
        }

        return best;
    }

    private static NearbyArcSupport NearbyRadialDoorLeafArcSupport(
        WallSegment wall,
        PlanPage page,
        ScannerOptions options)
    {
        var searchBounds = wall.Bounds.Inflate(Math.Max(options.MaxOpeningGap * 0.75, options.WallSnapTolerance * 8.0));
        var best = NearbyArcSupport.Empty;

        for (var index = 0; index < page.Primitives.Count; index++)
        {
            var primitive = page.Primitives[index];
            if (!TryResolveDoorSwingArcPrimitive(primitive, options, out var arc)
                || !arc.Bounds.Intersects(searchBounds)
                || !IsPlausibleDoorSwingArc(arc, options))
            {
                continue;
            }

            var hingeTolerance = Math.Max(
                Math.Max(options.WallSnapTolerance * 2.0, options.DefaultWallThickness * 2.0),
                arc.Radius * 0.14);
            var tipTolerance = Math.Max(
                Math.Max(options.WallSnapTolerance * 2.5, options.DefaultWallThickness * 2.0),
                arc.Radius * 0.18);
            var score = Math.Max(
                DoorLeafEndpointScore(wall.CenterLine.Start, wall.CenterLine.End, arc, hingeTolerance, tipTolerance),
                DoorLeafEndpointScore(wall.CenterLine.End, wall.CenterLine.Start, arc, hingeTolerance, tipTolerance));

            if (score > best.Score)
            {
                best = new NearbyArcSupport(
                    Math.Min(score, 0.98),
                    primitive.SourceId ?? primitive.Source.SourceId ?? $"p{page.Number}:primitive:{index}",
                    arc.Bounds);
            }
        }

        return best;
    }

    private static double DoorLeafEndpointScore(
        PlanPoint hingeCandidate,
        PlanPoint leafTipCandidate,
        ArcPrimitive arc,
        double hingeTolerance,
        double tipTolerance)
    {
        var hingeDistance = hingeCandidate.DistanceTo(arc.Center);
        if (hingeDistance > hingeTolerance)
        {
            return 0;
        }

        var tipRadiusError = Math.Abs(leafTipCandidate.DistanceTo(arc.Center) - arc.Radius);
        var lengthRadiusError = Math.Abs(hingeCandidate.DistanceTo(leafTipCandidate) - arc.Radius);
        var score = 0.48;
        if (tipRadiusError <= tipTolerance)
        {
            score += 0.28;
        }

        if (lengthRadiusError <= tipTolerance)
        {
            score += 0.18;
        }

        if (Math.Abs(arc.SweepAngleRadians) >= Math.PI * 0.35)
        {
            score += 0.08;
        }

        return score;
    }

    private static bool IsPlausibleDoorSwingArc(ArcPrimitive arc, ScannerOptions options)
        => DoorSwingArcRecovery.IsPlausibleDoorSwingArc(arc, options);

    private static bool TryResolveDoorSwingArcPrimitive(
        PlanPrimitive primitive,
        ScannerOptions options,
        out ArcPrimitive arc)
    {
        if (primitive is ArcPrimitive directArc)
        {
            arc = directArc;
            return true;
        }

        if (primitive is PolylinePrimitive polyline)
        {
            return DoorSwingArcRecovery.TryRecoverFromPolyline(
                polyline,
                options,
                DoorSwingArcRecoveryProfile.WallNoiseRejection,
                out arc);
        }

        arc = default!;
        return false;
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

    private static bool IsSurfacePatternLayerCategory(LayerCategory category) =>
        category == LayerCategory.SurfacePattern;

    private static bool IsObjectOrFixtureLayerCategory(LayerCategory category) =>
        category is LayerCategory.Equipment
            or LayerCategory.Electrical
            or LayerCategory.HVAC
            or LayerCategory.Plumbing
            or LayerCategory.FireSafety
            or LayerCategory.Furniture
            or LayerCategory.Fixture;

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
        var recoveredWallBandCount = recoveredWalls.Count(wall => wall.PairEvidence is not null);
        var recoveredShortWallCount = recoveredWalls.Count(wall =>
            wall.PairEvidence is null
            && wall.Evidence.Any(item => item.Contains("short supported wall segment", StringComparison.OrdinalIgnoreCase)));
        var continuityPromotedAssessments = assessments
            .Where(assessment => assessment.Evidence.Any(item =>
                item.Contains("continuity-supported short paired wall body", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

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
                ["sourceCandidateWallCount"] = originalWallCount.ToString(CultureInfo.InvariantCulture),
                ["recoveredCandidateWallCount"] = recoveredWalls.Count.ToString(CultureInfo.InvariantCulture),
                ["totalCandidateWallCount"] = (originalWallCount + recoveredWalls.Count).ToString(CultureInfo.InvariantCulture),
                ["outputWallCount"] = context.Walls.Count.ToString(CultureInfo.InvariantCulture),
                ["wallAssessmentCount"] = assessments.Count.ToString(CultureInfo.InvariantCulture),
                ["wallBandCount"] = bands.Count.ToString(CultureInfo.InvariantCulture),
                ["placementReadyWallCount"] = assessments.Count(item => item.PlacementReady).ToString(CultureInfo.InvariantCulture),
                ["reviewWallCount"] = assessments.Count(item => item.RequiresReview && !item.RejectedAsNoise).ToString(CultureInfo.InvariantCulture),
                ["recoveredWallCount"] = recoveredWalls.Count.ToString(CultureInfo.InvariantCulture),
                ["recoveredWallBandCount"] = recoveredWallBandCount.ToString(CultureInfo.InvariantCulture),
                ["recoveredShortWallCount"] = recoveredShortWallCount.ToString(CultureInfo.InvariantCulture),
                ["rejectedNoiseWallCount"] = rejectedWallIds.Count.ToString(CultureInfo.InvariantCulture),
                ["strongWallBodyCount"] = assessments.Count(item => item.Category == WallEvidenceCategory.StrongWallBody).ToString(CultureInfo.InvariantCulture),
                ["mediumWallBodyCount"] = assessments.Count(item => item.Category == WallEvidenceCategory.MediumWallBody).ToString(CultureInfo.InvariantCulture),
                ["weakSingleLineCount"] = assessments.Count(item => item.Category == WallEvidenceCategory.WeakSingleLine).ToString(CultureInfo.InvariantCulture)
            });

        if (continuityPromotedAssessments.Length > 0)
        {
            context.AddDiagnostic(
                "wall_evidence.continuity_supported_pairs_promoted",
                DiagnosticSeverity.Info,
                StageName,
                $"{continuityPromotedAssessments.Length} short paired wall candidate(s) were promoted by collinear structural continuity evidence.",
                region: PlanRect.Union(continuityPromotedAssessments.Select(item => item.Bounds)),
                confidence: Confidence.Medium,
                scope: DiagnosticScope.Detection,
                sourcePrimitiveIds: continuityPromotedAssessments.SelectMany(item => item.SourcePrimitiveIds),
                properties: new Dictionary<string, string>
                {
                    ["promotedWallCount"] = continuityPromotedAssessments.Length.ToString(CultureInfo.InvariantCulture),
                    ["wallIds"] = string.Join(",", continuityPromotedAssessments.Select(item => item.WallId).Order(StringComparer.Ordinal).Take(20))
                });
        }

        if (recoveredWalls.Count > 0)
        {
            context.AddDiagnostic(
                "wall_evidence.missing_wall_bands_recovered",
                DiagnosticSeverity.Info,
                StageName,
                $"{recoveredWalls.Count} missing wall candidate(s) were recovered from source primitive evidence.",
                region: PlanRect.Union(recoveredWalls.Select(wall => wall.Bounds)),
                confidence: Confidence.Medium,
                scope: DiagnosticScope.Detection,
                sourcePrimitiveIds: recoveredWalls.SelectMany(wall => wall.SourcePrimitiveIds),
                properties: new Dictionary<string, string>
                {
                    ["recoveredWallCount"] = recoveredWalls.Count.ToString(CultureInfo.InvariantCulture),
                    ["recoveredWallBandCount"] = recoveredWallBandCount.ToString(CultureInfo.InvariantCulture),
                    ["recoveredShortWallCount"] = recoveredShortWallCount.ToString(CultureInfo.InvariantCulture),
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
                    ["objectFixtureRejectedCount"] = rejectedAssessments.Count(item => item.Category == WallEvidenceCategory.ObjectOrFixtureDetail).ToString(CultureInfo.InvariantCulture),
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

    private readonly record struct FaceFragmentCounts(
        int MaxFaceFragments,
        int TotalFaceFragments);

    private enum WallOrientation
    {
        Unknown,
        Horizontal,
        Vertical
    }
}
