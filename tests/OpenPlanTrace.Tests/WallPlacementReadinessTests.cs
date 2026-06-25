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
    public void Evaluate_AllowsTrustedRecoveredRoomBoundaryObjectLikeWall()
    {
        var wall = Wall("wall:recovered-object-like-boundary", Confidence.High) with
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 96), new PlanPoint(300, 96)),
                new PlanLineSegment(new PlanPoint(100, 104), new PlanPoint(300, 104)),
                FaceSeparation: 8,
                OverlapRatio: 0.96,
                Score: 0.9,
                FirstFaceFragmentCount: 3,
                SecondFaceFragmentCount: 3,
                FirstFaceSourcePrimitiveIds: ["recovered-object-like-face-a"],
                SecondFaceSourcePrimitiveIds: ["recovered-object-like-face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "wall evidence: strong double-edge wall body",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: reclassified as object/fixture detail because graph component page:1:wall-component:9 is ObjectLikeIsland",
                "wall evidence: component excluded from structural topology as compact object-like linework",
                "wall evidence: rejected room-boundary candidate restored after unsupported indoor room edge scan",
                "wall evidence: rejected room-boundary recovery pair score 0.9, overlap ratio 0.96, side room hits 1"
            ]
        };
        var component = Component(
            WallGraphComponentKind.ObjectLikeIsland,
            excludedFromStructuralTopology: true,
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
        Assert.DoesNotContain("wall component excluded from structural topology", readiness.Reasons);
        Assert.DoesNotContain("wall belongs to compact object-like linework component", readiness.Reasons);
    }

    [Fact]
    public void Evaluate_BlocksObjectLikeWallWithoutRecoveredRoomBoundaryEvidence()
    {
        var wall = Wall("wall:object-like-without-recovery", Confidence.High) with
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 96), new PlanPoint(300, 96)),
                new PlanLineSegment(new PlanPoint(100, 104), new PlanPoint(300, 104)),
                FaceSeparation: 8,
                OverlapRatio: 0.96,
                Score: 0.9,
                FirstFaceFragmentCount: 3,
                SecondFaceFragmentCount: 3,
                FirstFaceSourcePrimitiveIds: ["object-like-face-a"],
                SecondFaceSourcePrimitiveIds: ["object-like-face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "wall evidence: strong double-edge wall body",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: reclassified as object/fixture detail because graph component page:1:wall-component:9 is ObjectLikeIsland",
                "wall evidence: component excluded from structural topology as compact object-like linework"
            ]
        };
        var component = Component(
            WallGraphComponentKind.ObjectLikeIsland,
            excludedFromStructuralTopology: true,
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
    public void Evaluate_AllowsRoomConfirmedIsolatedInteriorBoundary()
    {
        var wall = Wall("wall:isolated-room-boundary", Confidence.High) with
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            Evidence =
            [
                "parallel wall-face pair",
                "wall evidence: room-confirmed wall body promoted to placement-ready after room adjacency refinement",
                "wall evidence: room references 2, shared adjacency False, two-sided room evidence False, topology-supported endpoints 0",
                $"wall evidence: {WallPlacementReadinessEvaluator.RoomConfirmedIsolatedFragmentPromotionEvidence} because room boundary evidence overrode early isolated graph classification"
            ]
        };
        var component = Component(
            WallGraphComponentKind.IsolatedFragment,
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
        Assert.DoesNotContain("wall belongs to isolated wall graph fragment", readiness.Reasons);
    }

    [Fact]
    public void Evaluate_AllowsTrustedTwoSidedRoomBoundaryIsolatedInteriorBody()
    {
        var wall = Wall("wall:two-sided-isolated-room-boundary", Confidence.High) with
        {
            CenterLine = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 192)),
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(96, 100), new PlanPoint(96, 192)),
                new PlanLineSegment(new PlanPoint(104, 100), new PlanPoint(104, 192)),
                FaceSeparation: 8,
                OverlapRatio: 1,
                Score: 0.96,
                FirstFaceFragmentCount: 4,
                SecondFaceFragmentCount: 14,
                FirstFaceSourcePrimitiveIds: ["two-sided-face-a"],
                SecondFaceSourcePrimitiveIds: ["two-sided-face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "layer evidence: contains dimension-like text",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall type refined interior: detected room evidence on both sides"
            ]
        };
        var component = Component(
            WallGraphComponentKind.IsolatedFragment,
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
        Assert.DoesNotContain("wall belongs to isolated wall graph fragment", readiness.Reasons);
    }

    [Fact]
    public void Evaluate_BlocksTwoSidedRoomBoundaryIsolatedInteriorBodyWithWeakPair()
    {
        var wall = Wall("wall:weak-two-sided-isolated-room-boundary", Confidence.High) with
        {
            CenterLine = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 192)),
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(96, 100), new PlanPoint(96, 192)),
                new PlanLineSegment(new PlanPoint(104, 100), new PlanPoint(104, 192)),
                FaceSeparation: 8,
                OverlapRatio: 1,
                Score: 0.74,
                FirstFaceFragmentCount: 4,
                SecondFaceFragmentCount: 14,
                FirstFaceSourcePrimitiveIds: ["weak-two-sided-face-a"],
                SecondFaceSourcePrimitiveIds: ["weak-two-sided-face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall type refined interior: detected room evidence on both sides"
            ]
        };
        var component = Component(
            WallGraphComponentKind.IsolatedFragment,
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
        Assert.Contains("wall belongs to isolated wall graph fragment", readiness.Reasons);
    }

    [Fact]
    public void Evaluate_AllowsTrustedExteriorShellContinuityIsolatedFragment()
    {
        var wall = Wall("wall:trusted-exterior-shell-gap", Confidence.High) with
        {
            CenterLine = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(191, 100)),
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 96), new PlanPoint(191, 96)),
                new PlanLineSegment(new PlanPoint(100, 104), new PlanPoint(191, 104)),
                FaceSeparation: 8,
                OverlapRatio: 1,
                Score: 0.616,
                FirstFaceFragmentCount: 8,
                SecondFaceFragmentCount: 124,
                FirstFaceSourcePrimitiveIds: ["trusted-shell-face-a"],
                SecondFaceSourcePrimitiveIds: ["trusted-shell-face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                $"wall evidence: {WallPlacementReadinessEvaluator.TrustedExteriorShellContinuityEvidence}"
            ]
        };
        var component = Component(
            WallGraphComponentKind.IsolatedFragment,
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
        Assert.DoesNotContain("wall belongs to isolated wall graph fragment", readiness.Reasons);
    }

    [Fact]
    public void Evaluate_BlocksTrustedExteriorShellContinuityFragmentNearCoveredEntryEvidence()
    {
        var wall = Wall("wall:covered-entry-shell-gap", Confidence.High) with
        {
            CenterLine = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(191, 100)),
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 96), new PlanPoint(191, 96)),
                new PlanLineSegment(new PlanPoint(100, 104), new PlanPoint(191, 104)),
                FaceSeparation: 8,
                OverlapRatio: 1,
                Score: 0.616,
                FirstFaceFragmentCount: 8,
                SecondFaceFragmentCount: 124,
                FirstFaceSourcePrimitiveIds: ["covered-shell-face-a"],
                SecondFaceSourcePrimitiveIds: ["covered-shell-face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "outdoor covered-area boundary near overbygd entry",
                $"wall evidence: {WallPlacementReadinessEvaluator.TrustedExteriorShellContinuityEvidence}"
            ]
        };
        var component = Component(
            WallGraphComponentKind.IsolatedFragment,
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
    public void Evaluate_AllowsHighTrustSecondaryThinExteriorPairWithOneSidedRoomEvidence()
    {
        var wall = Wall("wall:secondary-thin-room-backed-exterior", Confidence.High) with
        {
            CenterLine = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(218, 100)),
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            Thickness = 2.9,
            ThicknessMillimeters = 51,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 98.55), new PlanPoint(218, 98.55)),
                new PlanLineSegment(new PlanPoint(100, 101.45), new PlanPoint(218, 101.45)),
                FaceSeparation: 2.9,
                OverlapRatio: 0.97,
                Score: 0.9,
                FirstFaceFragmentCount: 38,
                SecondFaceFragmentCount: 14,
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
            WallGraphComponentKind.SecondaryStructural,
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
    public void Evaluate_AllowsTrustedMainStructuralThinExteriorBridge()
    {
        var wall = Wall("wall:trusted-thin-exterior-bridge", Confidence.High) with
        {
            CenterLine = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(225, 100)),
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            Thickness = 2.95,
            ThicknessMillimeters = 52,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 98.525), new PlanPoint(225, 98.525)),
                new PlanLineSegment(new PlanPoint(100, 101.475), new PlanPoint(225, 101.475)),
                FaceSeparation: 2.95,
                OverlapRatio: 1,
                Score: 0.813,
                FirstFaceFragmentCount: 42,
                SecondFaceFragmentCount: 15,
                FirstFaceSourcePrimitiveIds: ["bridge-face-a"],
                SecondFaceSourcePrimitiveIds: ["bridge-face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "layer (unlayered) classified Unknown (0,35)",
                "layer evidence: no strong layer name or geometry evidence",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "trimmed 1 supported endpoint overrun(s) from wall centerline"
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
    public void Evaluate_AllowsSecondaryThinExteriorPairWithShellContinuity()
    {
        var wall = Wall("wall:secondary-thin-exterior-shell-continuity", Confidence.High) with
        {
            CenterLine = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(225, 100)),
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            Thickness = 2.95,
            ThicknessMillimeters = 52,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 98.525), new PlanPoint(225, 98.525)),
                new PlanLineSegment(new PlanPoint(100, 101.475), new PlanPoint(225, 101.475)),
                FaceSeparation: 2.95,
                OverlapRatio: 1,
                Score: 0.813,
                FirstFaceFragmentCount: 42,
                SecondFaceFragmentCount: 15,
                FirstFaceSourcePrimitiveIds: ["bridge-face-a"],
                SecondFaceSourcePrimitiveIds: ["bridge-face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "layer (unlayered) classified Unknown (0,35)",
                "layer evidence: no strong layer name or geometry evidence",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                $"wall evidence: {WallPlacementReadinessEvaluator.TrustedExteriorShellContinuityEvidence}"
            ]
        };
        var component = Component(
            WallGraphComponentKind.SecondaryStructural,
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
    public void Evaluate_BlocksTrustedThinExteriorBridgeWhenCoveredEntryEvidenceIsPresent()
    {
        var wall = Wall("wall:covered-thin-exterior-bridge", Confidence.High) with
        {
            CenterLine = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(225, 100)),
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            Thickness = 2.95,
            ThicknessMillimeters = 52,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 98.525), new PlanPoint(225, 98.525)),
                new PlanLineSegment(new PlanPoint(100, 101.475), new PlanPoint(225, 101.475)),
                FaceSeparation: 2.95,
                OverlapRatio: 1,
                Score: 0.813,
                FirstFaceFragmentCount: 42,
                SecondFaceFragmentCount: 15,
                FirstFaceSourcePrimitiveIds: ["covered-bridge-face-a"],
                SecondFaceSourcePrimitiveIds: ["covered-bridge-face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "layer (unlayered) classified Unknown (0,35)",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "trimmed 1 supported endpoint overrun(s) from wall centerline",
                "outdoor covered-area boundary near overbygd entry"
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
    public void Evaluate_AllowsTopologySupportedFragmentedPairWithGeometricRoomBoundarySupport()
    {
        var sourceIds = Enumerable.Range(1, 34)
            .Select(index => $"pdf:p1:path:{index}:line:1")
            .ToArray();
        var wall = Wall("wall:noisy-topology-supported-room-boundary", Confidence.High) with
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
                "wall evidence: pair score 0.718, max face fragments 78, total face fragments 85, topology-supported endpoints 2",
                "wall evidence: geometric room boundary support from reliable room-boundary alignment"
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

        Assert.True(readiness.ReadyForCoordinatePlacement);
        Assert.True(readiness.ReadyForMetricPlacement);
        Assert.False(readiness.RequiresReview);
        Assert.False(readiness.CoordinatePlacementBlocked);
        Assert.DoesNotContain(
            WallPlacementReadinessEvaluator.NoisyTopologySupportedFragmentedPairReason,
            readiness.Reasons);
    }

    [Fact]
    public void Evaluate_AllowsTopologySupportedFragmentedPairWithHighEndpointSupport()
    {
        var sourceIds = Enumerable.Range(1, 34)
            .Select(index => $"pdf:p1:path:{index}:line:1")
            .ToArray();
        var wall = Wall("wall:noisy-topology-supported-four-endpoints", Confidence.High) with
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
                "wall evidence: pair score 0.718, max face fragments 78, total face fragments 85, topology-supported endpoints 4"
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

        Assert.True(readiness.ReadyForCoordinatePlacement);
        Assert.True(readiness.ReadyForMetricPlacement);
        Assert.False(readiness.RequiresReview);
        Assert.False(readiness.CoordinatePlacementBlocked);
        Assert.DoesNotContain(
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
    public void Evaluate_AllowsWeakPromotedFragmentRoomBoundaryWithStructuralEndpointSupport()
    {
        var wall = Wall("wall:structural-context-promoted-fragment-boundary", Confidence.High) with
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
            Evidence = wall.Evidence,
            ScoreBreakdown = new WallEvidenceScoreBreakdown(
                PositiveScore: 0.82,
                NegativeScore: 0.14,
                DecisionScore: 0.68,
                PairSupportScore: 0,
                LayerSupportScore: 0,
                StructuralSupportScore: 0.85,
                RecoverySupportScore: 0,
                NoisePenalty: 0,
                FragmentReviewPenalty: 0,
                PositiveEvidence: ["both endpoints supported by structural context"],
                NegativeEvidence: Array.Empty<string>())
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

    [Fact]
    public void Evaluate_AllowsCleanReviewOnlyLongOneEndpointMainStructuralFragment()
    {
        var wall = Wall("wall:clean-one-end-fragment", Confidence.High) with
        {
            WallType = WallType.Interior,
            DetectionKind = WallDetectionKind.FragmentMerged,
            SourcePrimitiveIds = Enumerable.Range(1, 10)
                .Select(index => $"fragment-source-{index}")
                .ToArray(),
            FragmentEvidence = new WallFragmentEvidence(
                FragmentCount: 10,
                TotalHealedGap: 0,
                MaxHealedGap: 0,
                DuplicatePrimitiveCount: 0,
                GapRatio: 0,
                RequiresGeometryReview: false,
                Evidence: Array.Empty<string>()),
            Evidence =
            [
                "merged collinear wall fragments",
                "run merged 10 fragments",
                "wall type interior: supported wall evidence inside exterior envelope",
                "one endpoint supported by structural context",
                "wall evidence: unlayered fragment-merged wall candidate has only one trusted structural endpoint (10 fragments, gap ratio 0); keep for topology but block exact placement until reviewed"
            ]
        };
        var component = Component(
            WallGraphComponentKind.MainStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.MediumWallBody, placementReady: false) with
        {
            Evidence = wall.Evidence,
            Decision = WallEvidenceDecision.Review
        };

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence,
            [
                WallPlacementContextGuards.FragmentMergedInteriorWithoutRoomBoundarySupportReason,
                WallPlacementContextGuards.MainStructuralInteriorWithoutSemanticSupportReason
            ]);

        Assert.True(readiness.ReadyForCoordinatePlacement);
        Assert.True(readiness.ReadyForMetricPlacement);
        Assert.False(readiness.RequiresReview);
        Assert.False(readiness.CoordinatePlacementBlocked);
        Assert.DoesNotContain(
            WallPlacementContextGuards.FragmentMergedInteriorWithoutRoomBoundarySupportReason,
            readiness.Reasons);
    }

    [Fact]
    public void Evaluate_AllowsDenseTwoSidedRoomFragmentWithOneSupportedEndpoint()
    {
        var wall = Wall("wall:dense-two-sided-room-fragment", Confidence.High) with
        {
            WallType = WallType.Interior,
            DetectionKind = WallDetectionKind.FragmentMerged,
            SourcePrimitiveIds = Enumerable.Range(1, 38)
                .Select(index => $"dense-fragment-source-{index}")
                .ToArray(),
            FragmentEvidence = new WallFragmentEvidence(
                FragmentCount: 38,
                TotalHealedGap: 0,
                MaxHealedGap: 0,
                DuplicatePrimitiveCount: 9,
                GapRatio: 0,
                RequiresGeometryReview: false,
                Evidence: Array.Empty<string>()),
            Evidence =
            [
                "merged collinear wall fragments",
                "run merged 38 fragments",
                "layer evidence: contains dimension-like text",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall type refined interior: detected room evidence on both sides",
                "one endpoint supported by structural context",
                "wall evidence: unlayered fragment-merged wall candidate has only one trusted structural endpoint (38 fragments, gap ratio 0); keep for topology but block exact placement until reviewed",
                "wall evidence: room-confirmed wall body promoted to placement-ready after room adjacency refinement",
                "wall evidence: clean fragment-merged interior room boundary promoted after room refinement confirmed it belongs to a detected room boundary"
            ]
        };
        var component = Component(
            WallGraphComponentKind.SecondaryStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.MediumWallBody, placementReady: true) with
        {
            Evidence = wall.Evidence,
            Decision = WallEvidenceDecision.Accept
        };

        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            ReliableCalibration(),
            component,
            evidence,
            [WallPlacementContextGuards.SecondaryStructuralWithoutRoomBoundarySupportReason]);

        Assert.True(readiness.ReadyForCoordinatePlacement);
        Assert.True(readiness.ReadyForMetricPlacement);
        Assert.False(readiness.RequiresReview);
        Assert.False(readiness.CoordinatePlacementBlocked);
    }

    [Fact]
    public void Evaluate_BlocksCoveredEntryBoundaryWithoutShellSupport()
    {
        var wall = Wall("wall:covered-entry-boundary", Confidence.High) with
        {
            WallType = WallType.Unknown,
            DetectionKind = WallDetectionKind.ParallelLinePair,
            Evidence =
            [
                "wall type refined unknown: shared outdoor/terrace room evidence is not trusted as exterior without shell support; outdoor covered-area boundary",
                "layer evidence: no strong layer name or geometry evidence"
            ]
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
    public void Evaluate_AllowsTrustedMainStructuralExteriorWallBodyWithReviewEvidence()
    {
        var wall = Wall("wall:trusted-main-exterior-review", Confidence.High) with
        {
            WallType = WallType.Exterior,
            DetectionKind = WallDetectionKind.ParallelLinePair,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 96), new PlanPoint(300, 96)),
                new PlanLineSegment(new PlanPoint(100, 104), new PlanPoint(300, 104)),
                FaceSeparation: 8,
                OverlapRatio: 0.98,
                Score: 0.88,
                FirstFaceFragmentCount: 36,
                SecondFaceFragmentCount: 52,
                FirstFaceSourcePrimitiveIds: ["trusted-main-exterior-face-a"],
                SecondFaceSourcePrimitiveIds: ["trusted-main-exterior-face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "filled wall-solid primitive",
                "wall evidence: filled closed vector wall body",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary"
            ]
        };
        var component = Component(
            WallGraphComponentKind.MainStructural,
            excludedFromStructuralTopology: false,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.MediumWallBody, placementReady: false) with
        {
            Evidence = wall.Evidence,
            Decision = WallEvidenceDecision.Review,
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
                new[] { "not placement-ready without review" })
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
        Assert.DoesNotContain("wall evidence requires review (MediumWallBody)", readiness.Reasons);
    }

    [Fact]
    public void Evaluate_AllowsTrustedLongIsolatedExteriorShellWallBodyWithReviewEvidence()
    {
        var wall = Wall("wall:trusted-isolated-exterior-shell", Confidence.High) with
        {
            WallType = WallType.Unknown,
            DetectionKind = WallDetectionKind.ParallelLinePair,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 96), new PlanPoint(300, 96)),
                new PlanLineSegment(new PlanPoint(100, 104), new PlanPoint(300, 104)),
                FaceSeparation: 8,
                OverlapRatio: 0.99,
                Score: 0.93,
                FirstFaceFragmentCount: 72,
                SecondFaceFragmentCount: 39,
                FirstFaceSourcePrimitiveIds: ["trusted-isolated-exterior-face-a"],
                SecondFaceSourcePrimitiveIds: ["trusted-isolated-exterior-face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "filled wall-solid primitive",
                "wall evidence: filled closed vector wall body",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary"
            ]
        };
        var component = Component(
            WallGraphComponentKind.IsolatedFragment,
            excludedFromStructuralTopology: true,
            wall.Id);
        var evidence = Evidence(wall, WallEvidenceCategory.MediumWallBody, placementReady: false) with
        {
            Evidence = wall.Evidence,
            Decision = WallEvidenceDecision.Review,
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
                new[] { "not placement-ready without review" })
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
        Assert.DoesNotContain("wall component excluded from structural topology", readiness.Reasons);
        Assert.DoesNotContain("wall belongs to isolated wall graph fragment", readiness.Reasons);
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
