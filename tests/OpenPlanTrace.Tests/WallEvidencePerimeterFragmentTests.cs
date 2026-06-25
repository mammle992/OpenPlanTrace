namespace OpenPlanTrace.Tests;

public sealed class WallEvidencePerimeterFragmentTests
{
    [Fact]
    public async Task WallEvidenceRefinement_MarksDimensionLikeFragmentedPerimeterPairReviewOnly()
    {
        var wall = new WallSegment(
            "wall-dimension-like-fragmented-perimeter",
            1,
            new PlanLineSegment(new PlanPoint(100, 80), new PlanPoint(300, 80)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            SourcePrimitiveIds = Enumerable.Range(1, 18).Select(index => $"dim-frag-{index}").ToArray(),
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 77), new PlanPoint(300, 77)),
                new PlanLineSegment(new PlanPoint(100, 83), new PlanPoint(300, 83)),
                FaceSeparation: 6,
                OverlapRatio: 1,
                Score: 0.94,
                FirstFaceFragmentCount: 4,
                SecondFaceFragmentCount: 46,
                FirstFaceSourcePrimitiveIds: ["dim-frag-a"],
                SecondFaceSourcePrimitiveIds: ["dim-frag-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "pair score 0,94",
                "first face merged 4 fragments",
                "second face merged 46 fragments",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary"
            ]
        };
        var context = new ScanContext(
            new PlanDocument(
                "dimension-like-fragmented-perimeter-pair",
                [new PlanPage(1, new PlanSize(420, 220), Array.Empty<PlanPrimitive>())]),
            new ScannerOptions());
        context.WallCandidates.Add(wall);

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.Equal(WallEvidenceCategory.MediumWallBody, assessment.Category);
        Assert.Equal(WallEvidenceDecision.Review, assessment.Decision);
        Assert.False(assessment.PlacementReady);
        Assert.True(assessment.RequiresReview);
        Assert.False(assessment.RejectedAsNoise);
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("dimension-like fragmented perimeter parallel-face candidate", StringComparison.OrdinalIgnoreCase)
                && item.Contains("max face fragments 46", StringComparison.OrdinalIgnoreCase));
    }
}
