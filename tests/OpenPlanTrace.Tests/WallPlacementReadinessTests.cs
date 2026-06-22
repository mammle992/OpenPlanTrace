namespace OpenPlanTrace.Tests;

public sealed class WallPlacementReadinessTests
{
    [Fact]
    public void Evaluate_BlocksObjectLikeComponentFromCoordinatePlacement()
    {
        var wall = Wall("wall:fixture", Confidence.High);
        var component = Component(
            WallGraphComponentKind.ObjectLikeIsland,
            excludedFromStructuralTopology: true,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.StrongWallBody, placementReady: true);

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.False(readiness.ReadyForCoordinatePlacement);
        Assert.False(readiness.ReadyForMetricPlacement);
        Assert.True(readiness.RequiresReview);
        Assert.True(readiness.CoordinatePlacementBlocked);
        Assert.Contains("wall component excluded from structural topology", readiness.Reasons);
        Assert.Contains("wall belongs to compact object-like linework component", readiness.Reasons);
    }

    [Fact]
    public void Evaluate_BlocksIsolatedFragmentFromCoordinatePlacement()
    {
        var wall = Wall("wall:isolated-fragment", Confidence.High);
        var component = Component(
            WallGraphComponentKind.IsolatedFragment,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.StrongWallBody, placementReady: true);

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.False(readiness.ReadyForCoordinatePlacement);
        Assert.False(readiness.ReadyForMetricPlacement);
        Assert.True(readiness.RequiresReview);
        Assert.True(readiness.CoordinatePlacementBlocked);
        Assert.Contains("wall belongs to isolated wall graph fragment", readiness.Reasons);
    }

    [Fact]
    public void Evaluate_AllowsStrongStructuralWallWithReliableScale()
    {
        var wall = Wall("wall:structural", Confidence.High);
        var component = Component(
            WallGraphComponentKind.MainStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.StrongWallBody, placementReady: true);

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.True(readiness.ReadyForCoordinatePlacement);
        Assert.True(readiness.ReadyForMetricPlacement);
        Assert.False(readiness.RequiresReview);
        Assert.False(readiness.CoordinatePlacementBlocked);
        Assert.Empty(readiness.Reasons);
    }

    [Fact]
    public void Evaluate_BlocksFragmentGeometryThatNeedsReview()
    {
        var wall = Wall("wall:fragment", Confidence.Medium) with
        {
            FragmentEvidence = new WallFragmentEvidence(
                4,
                24,
                9,
                0,
                0.42,
                true,
                new[] { "fragment merge healed multiple gaps" })
        };
        var component = Component(
            WallGraphComponentKind.SecondaryStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.MediumWallBody, placementReady: true);

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.False(readiness.ReadyForCoordinatePlacement);
        Assert.False(readiness.ReadyForMetricPlacement);
        Assert.True(readiness.RequiresReview);
        Assert.False(readiness.CoordinatePlacementBlocked);
        Assert.Contains("wall fragment geometry requires review before exact placement", readiness.Reasons);
    }

    [Fact]
    public void Evaluate_BlocksTopologyImportBlockedWallGraphRepairReasonFromCoordinatePlacement()
    {
        var wall = Wall("wall:repair-blocked", Confidence.High);
        var component = Component(
            WallGraphComponentKind.MainStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.StrongWallBody, placementReady: true);
        var repairReason =
            "wall graph repair candidate repair-1 requires review for endpoint-to-wall snap (EndpointToWall, TopologyImportBlocked, 17.238 drawing units)";

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence,
            new[] { repairReason });

        Assert.False(readiness.ReadyForCoordinatePlacement);
        Assert.False(readiness.ReadyForMetricPlacement);
        Assert.True(readiness.RequiresReview);
        Assert.True(readiness.CoordinatePlacementBlocked);
        Assert.Contains(repairReason, readiness.Reasons);
    }

    [Fact]
    public void Evaluate_BlocksRecoveredExteriorFromOneSidedRoomEvidenceWithoutShellSupport()
    {
        var wall = Wall("wall:recovered-one-sided-exterior", Confidence.High) with
        {
            WallType = WallType.Exterior,
            Evidence = new[]
            {
                "recovered by wall evidence map from unclaimed parallel wall-face evidence",
                "wall type refined exterior: detected room evidence on one side only"
            }
        };
        var component = Component(
            WallGraphComponentKind.MainStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.RecoveredWallBody, placementReady: true);

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.False(readiness.ReadyForCoordinatePlacement);
        Assert.False(readiness.ReadyForMetricPlacement);
        Assert.True(readiness.RequiresReview);
        Assert.True(readiness.CoordinatePlacementBlocked);
        Assert.Contains(
            "recovered exterior wall has only one-sided room evidence and no trusted exterior shell support",
            readiness.Reasons);
    }

    [Fact]
    public void Evaluate_AllowsRecoveredExteriorWhenShellSupportIsExplicit()
    {
        var wall = Wall("wall:recovered-shell-supported-exterior", Confidence.High) with
        {
            WallType = WallType.Exterior,
            Evidence = new[]
            {
                "recovered by wall evidence map from unclaimed parallel wall-face evidence",
                "wall type refined exterior: detected room evidence on one side only",
                "wall evidence: retained by exterior shell continuity"
            }
        };
        var component = Component(
            WallGraphComponentKind.MainStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.RecoveredWallBody, placementReady: true);

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.True(readiness.ReadyForCoordinatePlacement);
        Assert.True(readiness.ReadyForMetricPlacement);
        Assert.False(readiness.RequiresReview);
        Assert.False(readiness.CoordinatePlacementBlocked);
        Assert.DoesNotContain(
            "recovered exterior wall has only one-sided room evidence and no trusted exterior shell support",
            readiness.Reasons);
    }

    [Fact]
    public void Evaluate_BlocksUntrustedOutdoorBoundaryEvidenceFromCoordinatePlacement()
    {
        var wall = Wall("wall:untrusted-outdoor-boundary", Confidence.High) with
        {
            WallType = WallType.Unknown,
            Evidence = new[]
            {
                "wall type refined unknown: one-sided outdoor/terrace room evidence alone is not trusted as exterior shell support for uncertain local-boundary wall candidate",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary"
            }
        };
        var component = Component(
            WallGraphComponentKind.MainStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.MediumWallBody, placementReady: true) with
        {
            Evidence = wall.Evidence
        };

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.False(readiness.ReadyForCoordinatePlacement);
        Assert.False(readiness.ReadyForMetricPlacement);
        Assert.True(readiness.RequiresReview);
        Assert.True(readiness.CoordinatePlacementBlocked);
        Assert.Contains(
            "outdoor/terrace room evidence alone is not trusted as exterior wall placement support",
            readiness.Reasons);
    }

    [Fact]
    public void Evaluate_AllowsOutdoorBoundaryEvidenceWhenShellSupportIsExplicit()
    {
        var wall = Wall("wall:trusted-outdoor-shell-boundary", Confidence.High) with
        {
            WallType = WallType.Exterior,
            Evidence = new[]
            {
                "wall type refined exterior: room evidence on both sides includes outdoor/terrace space",
                "wall evidence: exterior shell continuity support"
            }
        };
        var component = Component(
            WallGraphComponentKind.MainStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.MediumWallBody, placementReady: true) with
        {
            Evidence = wall.Evidence
        };

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.True(readiness.ReadyForCoordinatePlacement);
        Assert.True(readiness.ReadyForMetricPlacement);
        Assert.False(readiness.RequiresReview);
        Assert.False(readiness.CoordinatePlacementBlocked);
    }

    [Fact]
    public void Evaluate_AllowsLongMainStructuralThinExteriorPairWithOneSidedRoomEvidence()
    {
        var wall = Wall("wall:long-thin-room-backed-exterior", Confidence.High) with
        {
            CenterLine = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(215, 100)),
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            Thickness = 2.9,
            ThicknessMillimeters = 51,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 98.55), new PlanPoint(215, 98.55)),
                new PlanLineSegment(new PlanPoint(100, 101.45), new PlanPoint(215, 101.45)),
                FaceSeparation: 2.9,
                OverlapRatio: 1,
                Score: 0.81,
                FirstFaceFragmentCount: 42,
                SecondFaceFragmentCount: 15,
                FirstFaceSourcePrimitiveIds: ["face-a"],
                SecondFaceSourcePrimitiveIds: ["face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "layer (unlayered) classified Unknown (0,35)",
                "layer evidence: no strong layer name or geometry evidence",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "wall type refined exterior: detected room evidence on one side only"
            ]
        };
        var component = Component(
            WallGraphComponentKind.MainStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.StrongWallBody, placementReady: true) with
        {
            Evidence = wall.Evidence
        };

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.True(readiness.ReadyForCoordinatePlacement);
        Assert.True(readiness.ReadyForMetricPlacement);
        Assert.False(readiness.RequiresReview);
        Assert.False(readiness.CoordinatePlacementBlocked);
        Assert.DoesNotContain(
            WallPlacementReadinessEvaluator.ThinExteriorFacePairWithoutShellSupportReason,
            readiness.Reasons);
    }

    [Fact]
    public void Evaluate_BlocksLongThinExteriorPairWhenOneSidedEvidenceIsOutdoor()
    {
        var wall = Wall("wall:long-thin-outdoor-backed-exterior", Confidence.High) with
        {
            CenterLine = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(215, 100)),
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            Thickness = 2.9,
            ThicknessMillimeters = 51,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 98.55), new PlanPoint(215, 98.55)),
                new PlanLineSegment(new PlanPoint(100, 101.45), new PlanPoint(215, 101.45)),
                FaceSeparation: 2.9,
                OverlapRatio: 1,
                Score: 0.81,
                FirstFaceFragmentCount: 42,
                SecondFaceFragmentCount: 15,
                FirstFaceSourcePrimitiveIds: ["face-a"],
                SecondFaceSourcePrimitiveIds: ["face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "layer (unlayered) classified Unknown (0,35)",
                "layer evidence: no strong layer name or geometry evidence",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "wall type refined exterior: detected room evidence on one side is outdoor/terrace space"
            ]
        };
        var component = Component(
            WallGraphComponentKind.MainStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.StrongWallBody, placementReady: true) with
        {
            Evidence = wall.Evidence
        };

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.False(readiness.ReadyForCoordinatePlacement);
        Assert.False(readiness.ReadyForMetricPlacement);
        Assert.True(readiness.RequiresReview);
        Assert.True(readiness.CoordinatePlacementBlocked);
        Assert.Contains(
            WallPlacementReadinessEvaluator.ThinExteriorFacePairWithoutShellSupportReason,
            readiness.Reasons);
    }

    [Fact]
    public void Evaluate_BlocksShortDenseUnknownLayerDetailCandidateFromCoordinatePlacement()
    {
        var sourceIds = Enumerable.Range(1, 34)
            .Select(index => $"pdf:p1:path:{index}:line:1")
            .ToArray();
        var wall = Wall("wall:short-dense-detail", Confidence.High) with
        {
            CenterLine = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(140, 100)),
            SourcePrimitiveIds = sourceIds,
            Evidence = new[]
            {
                "parallel wall-face pair",
                "first face merged 29 fragments",
                "first face collapsed 5 duplicate or near-duplicate wall line primitive(s)",
                "layer (unlayered) classified Unknown (0,35)"
            }
        };
        var component = Component(
            WallGraphComponentKind.MainStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.StrongWallBody, placementReady: true) with
        {
            Evidence = wall.Evidence,
            ScoreBreakdown = new WallEvidenceScoreBreakdown(
                0.7,
                0,
                0.7,
                0.5,
                0,
                0.2,
                0,
                0,
                0,
                new[] { "strong parallel-face wall pair", "both endpoints supported by structural context" },
                Array.Empty<string>())
        };

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.False(readiness.ReadyForCoordinatePlacement);
        Assert.False(readiness.ReadyForMetricPlacement);
        Assert.True(readiness.RequiresReview);
        Assert.True(readiness.CoordinatePlacementBlocked);
        Assert.Contains(
            "short high-density unknown-layer wall/detail candidate requires review before exact placement",
            readiness.Reasons);
    }

    [Fact]
    public void Evaluate_AllowsTopologySupportedFragmentedPairPromotionThroughShortDenseGate()
    {
        var sourceIds = Enumerable.Range(1, 34)
            .Select(index => $"pdf:p1:path:{index}:line:1")
            .ToArray();
        var wall = Wall("wall:topology-supported-fragmented-pair", Confidence.High) with
        {
            CenterLine = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(140, 100)),
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            SourcePrimitiveIds = sourceIds,
            Evidence =
            [
                "parallel wall-face pair",
                "first face merged 29 fragments",
                "first face collapsed 5 duplicate or near-duplicate wall line primitive(s)",
                "layer (unlayered) classified Unknown (0,35)",
                "wall evidence: topology-supported fragmented paired wall promoted after both endpoints aligned to trusted structural graph"
            ]
        };
        var component = Component(
            WallGraphComponentKind.MainStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.MediumWallBody, placementReady: true) with
        {
            Evidence = wall.Evidence,
            ScoreBreakdown = new WallEvidenceScoreBreakdown(
                0.7,
                0,
                0.7,
                0.5,
                0,
                0.2,
                0,
                0,
                0,
                new[] { "strong parallel-face wall pair", "both endpoints supported by structural context" },
                Array.Empty<string>())
        };

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.True(readiness.ReadyForCoordinatePlacement);
        Assert.True(readiness.ReadyForMetricPlacement);
        Assert.False(readiness.RequiresReview);
        Assert.False(readiness.CoordinatePlacementBlocked);
        Assert.DoesNotContain(
            "short high-density unknown-layer wall/detail candidate requires review before exact placement",
            readiness.Reasons);
    }

    [Fact]
    public void Evaluate_BlocksTopologySupportedFragmentedPairWhenOneFaceIsExtremelyFragmented()
    {
        var sourceIds = Enumerable.Range(1, 34)
            .Select(index => $"pdf:p1:path:{index}:line:1")
            .ToArray();
        var wall = Wall("wall:noisy-topology-supported-fragmented-pair", Confidence.High) with
        {
            CenterLine = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(146, 100)),
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            SourcePrimitiveIds = sourceIds,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 97), new PlanPoint(146, 97)),
                new PlanLineSegment(new PlanPoint(100, 103), new PlanPoint(146, 103)),
                FaceSeparation: 6,
                OverlapRatio: 1,
                Score: 0.718,
                FirstFaceFragmentCount: 7,
                SecondFaceFragmentCount: 78,
                FirstFaceSourcePrimitiveIds: ["face-a"],
                SecondFaceSourcePrimitiveIds: ["face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "layer (unlayered) classified Unknown (0,35)",
                "wall evidence: short unlayered parallel-face candidate has noisy fragmented face evidence (score 0.718, max face fragments 78, total face fragments 85); keep for topology but block exact placement until reviewed",
                "wall evidence: topology-supported fragmented paired wall promoted after both endpoints aligned to trusted structural graph",
                "wall evidence: pair score 0.718, max face fragments 78, total face fragments 85, topology-supported endpoints 2"
            ]
        };
        var component = Component(
            WallGraphComponentKind.MainStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.MediumWallBody, placementReady: true) with
        {
            Evidence = wall.Evidence,
            ScoreBreakdown = new WallEvidenceScoreBreakdown(
                0.718,
                0,
                0.718,
                0.5,
                0,
                0.2,
                0,
                0,
                0,
                new[] { "strong parallel-face wall pair", "both endpoints supported by structural context" },
                Array.Empty<string>())
        };

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.False(readiness.ReadyForCoordinatePlacement);
        Assert.False(readiness.ReadyForMetricPlacement);
        Assert.True(readiness.RequiresReview);
        Assert.True(readiness.CoordinatePlacementBlocked);
        Assert.Contains(
            WallPlacementReadinessEvaluator.NoisyTopologySupportedFragmentedPairReason,
            readiness.Reasons);
    }

    [Fact]
    public void Evaluate_AllowsShortDenseCandidateWithExplicitRoomBoundarySupport()
    {
        var sourceIds = Enumerable.Range(1, 34)
            .Select(index => $"pdf:p1:path:{index}:line:1")
            .ToArray();
        var wall = Wall("wall:short-dense-room-boundary", Confidence.High) with
        {
            CenterLine = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(140, 100)),
            SourcePrimitiveIds = sourceIds,
            Evidence = new[]
            {
                "parallel wall-face pair",
                "first face merged 29 fragments",
                "first face collapsed 5 duplicate or near-duplicate wall line primitive(s)",
                "layer (unlayered) classified Unknown (0,35)",
                "wall evidence: retained by room boundary support"
            }
        };
        var component = Component(
            WallGraphComponentKind.MainStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.StrongWallBody, placementReady: true) with
        {
            Evidence = wall.Evidence,
            ScoreBreakdown = new WallEvidenceScoreBreakdown(
                0.7,
                0,
                0.7,
                0.5,
                0,
                0.2,
                0,
                0,
                0,
                new[] { "strong parallel-face wall pair", "both endpoints supported by structural context" },
                Array.Empty<string>())
        };

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.True(readiness.ReadyForCoordinatePlacement);
        Assert.True(readiness.ReadyForMetricPlacement);
        Assert.False(readiness.RequiresReview);
        Assert.False(readiness.CoordinatePlacementBlocked);
    }

    [Fact]
    public void Evaluate_AllowsShortDenseCandidateWithTwoSidedRoomEvidence()
    {
        var sourceIds = Enumerable.Range(1, 34)
            .Select(index => $"pdf:p1:path:{index}:line:1")
            .ToArray();
        var wall = Wall("wall:short-dense-two-sided-room", Confidence.High) with
        {
            CenterLine = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(140, 100)),
            SourcePrimitiveIds = sourceIds,
            Evidence = new[]
            {
                "parallel wall-face pair",
                "first face merged 29 fragments",
                "first face collapsed 5 duplicate or near-duplicate wall line primitive(s)",
                "layer (unlayered) classified Unknown (0,35)",
                "wall type refined interior: detected room evidence on both sides"
            }
        };
        var component = Component(
            WallGraphComponentKind.MainStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.StrongWallBody, placementReady: true) with
        {
            Evidence = wall.Evidence,
            ScoreBreakdown = new WallEvidenceScoreBreakdown(
                0.7,
                0,
                0.7,
                0.5,
                0,
                0.2,
                0,
                0,
                0,
                new[] { "strong parallel-face wall pair", "both endpoints supported by structural context" },
                Array.Empty<string>())
        };

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.True(readiness.ReadyForCoordinatePlacement);
        Assert.True(readiness.ReadyForMetricPlacement);
        Assert.False(readiness.RequiresReview);
        Assert.False(readiness.CoordinatePlacementBlocked);
    }

    [Fact]
    public void Evaluate_BlocksWeakPromotedFragmentRoomBoundaryWithoutTopologySupport()
    {
        var wall = Wall("wall:weak-promoted-fragment-boundary", Confidence.High) with
        {
            WallType = WallType.Interior,
            DetectionKind = WallDetectionKind.FragmentMerged,
            FragmentEvidence = new WallFragmentEvidence(
                FragmentCount: 4,
                TotalHealedGap: 0,
                MaxHealedGap: 0,
                DuplicatePrimitiveCount: 0,
                GapRatio: 0,
                RequiresGeometryReview: false,
                Evidence: Array.Empty<string>()),
            Evidence = new[]
            {
                "wall evidence: room-confirmed wall body promoted to placement-ready after room adjacency refinement",
                "wall evidence: room references 1, shared adjacency False, two-sided room evidence False, topology-supported endpoints 0",
                "wall evidence: clean fragment-merged interior room boundary promoted after room refinement confirmed it belongs to a detected room boundary"
            }
        };
        var component = Component(
            WallGraphComponentKind.SecondaryStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.MediumWallBody, placementReady: true) with
        {
            Evidence = wall.Evidence
        };

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.False(readiness.ReadyForCoordinatePlacement);
        Assert.False(readiness.ReadyForMetricPlacement);
        Assert.True(readiness.RequiresReview);
        Assert.True(readiness.CoordinatePlacementBlocked);
        Assert.Contains(
            WallPlacementReadinessEvaluator.WeakPromotedFragmentRoomBoundaryReason,
            readiness.Reasons);
    }

    [Fact]
    public void Evaluate_AllowsPromotedFragmentRoomBoundaryWithGeometricSupport()
    {
        var wall = Wall("wall:geometric-promoted-fragment-boundary", Confidence.High) with
        {
            WallType = WallType.Interior,
            DetectionKind = WallDetectionKind.FragmentMerged,
            FragmentEvidence = new WallFragmentEvidence(
                FragmentCount: 4,
                TotalHealedGap: 0,
                MaxHealedGap: 0,
                DuplicatePrimitiveCount: 0,
                GapRatio: 0,
                RequiresGeometryReview: false,
                Evidence: Array.Empty<string>()),
            Evidence = new[]
            {
                "wall evidence: room-confirmed wall body promoted to placement-ready after room adjacency refinement",
                "wall evidence: room references 1, shared adjacency False, two-sided room evidence False, topology-supported endpoints 0",
                "wall evidence: clean fragment-merged interior room boundary promoted after room refinement confirmed it belongs to a detected room boundary",
                "wall evidence: geometric room boundary support from reliable room-boundary alignment"
            }
        };
        var component = Component(
            WallGraphComponentKind.SecondaryStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.MediumWallBody, placementReady: true) with
        {
            Evidence = wall.Evidence
        };

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence);

        Assert.True(readiness.ReadyForCoordinatePlacement);
        Assert.True(readiness.ReadyForMetricPlacement);
        Assert.False(readiness.RequiresReview);
        Assert.False(readiness.CoordinatePlacementBlocked);
        Assert.DoesNotContain(
            WallPlacementReadinessEvaluator.WeakPromotedFragmentRoomBoundaryReason,
            readiness.Reasons);
    }

    private static WallSegment Wall(string id, Confidence confidence) =>
        new(
            id,
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(300, 100)),
            12,
            confidence)
        {
            SourcePrimitiveIds = new[] { id }
        };

    private static WallGraphComponent Component(
        WallGraphComponentKind kind,
        bool excludedFromStructuralTopology,
        string wallId) =>
        new(
            $"component:{wallId}",
            1,
            kind,
            new PlanRect(96, 94, 208, 12),
            new[] { wallId },
            new[] { $"node:{wallId}:a", $"node:{wallId}:b" },
            new[] { $"edge:{wallId}" },
            new[] { wallId },
            200,
            Confidence.High,
            Array.Empty<string>(),
            excludedFromStructuralTopology);

    private static WallEvidenceWallAssessment Evidence(
        WallSegment wall,
        WallEvidenceCategory category,
        bool placementReady) =>
        new(
            wall.Id,
            wall.PageNumber,
            wall.Bounds,
            category,
            Confidence.High,
            placementReady,
            !placementReady,
            false,
            wall.SourcePrimitiveIds,
            Array.Empty<string>());

    private static PlanCalibration ReliableCalibration() =>
        new(
            PlanMeasurementUnit.PdfPoint,
            PlanMeasurementUnit.Millimeter,
            ScaleRatio: null,
            MillimetersPerDrawingUnit: 35,
            Confidence.High,
            Array.Empty<CalibrationEvidence>(),
            Array.Empty<CalibrationScaleGroup>());
}
