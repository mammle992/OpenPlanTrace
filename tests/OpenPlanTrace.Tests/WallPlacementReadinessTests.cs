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
