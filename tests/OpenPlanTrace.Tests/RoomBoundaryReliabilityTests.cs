namespace OpenPlanTrace.Tests;

public sealed class RoomBoundaryReliabilityTests
{
    [Fact]
    public void HasReliableBoundaryEvidence_BlocksWeakReviewSupportedSemanticRoom()
    {
        var room = Room(
            [
                "wall-a",
                "wall-b",
                "wall-c",
                "wall-d"
            ],
            "review-supported semantic room boundary inferred from nearby wall candidates wall-a,wall-b,wall-c,wall-d",
            "semantic room boundary wall coverage 0.75 across 3 strong side(s)",
            "semantic room boundary trusted wall support 0.2 across 1 side(s)");

        Assert.False(RoomBoundaryReliability.HasReliableBoundaryEvidence(room));
        Assert.True(RoomBoundaryReliability.IsWeakReviewSupportedSemanticBoundary(room));
    }

    [Fact]
    public void HasReliableBoundaryEvidence_AllowsStrongReviewSupportedSemanticRoom()
    {
        var room = Room(
            [
                "wall-a",
                "wall-b",
                "wall-c",
                "wall-d"
            ],
            "review-supported semantic room boundary inferred from nearby wall candidates wall-a,wall-b,wall-c,wall-d",
            "semantic room boundary wall coverage 0.84 across 4 strong side(s)",
            "semantic room boundary trusted wall support 0.5 across 2 side(s)");

        Assert.True(RoomBoundaryReliability.HasReliableBoundaryEvidence(room));
        Assert.False(RoomBoundaryReliability.IsWeakReviewSupportedSemanticBoundary(room));
    }

    [Fact]
    public void RoomBoundaryWallReferenceBuilder_DoesNotUseWeakReviewSupportedRoomWallIdsAsStructuralSupport()
    {
        var wall = Wall("wall-a", 0, 0, 0, 100);
        var room = Room(
            [wall.Id],
            "review-supported semantic room boundary inferred from nearby wall candidates wall-a,wall-b,wall-c,wall-d",
            "semantic room boundary wall coverage 0.75 across 3 strong side(s)",
            "semantic room boundary trusted wall support 0.2 across 1 side(s)");

        var result = RoomBoundaryWallReferenceBuilder.Build([room], [wall], wallSnapTolerance: 3);

        Assert.Empty(result.RoomIdsByWallId);
        Assert.Empty(result.GeometricRoomBoundaryWallIds);
    }

    private static RoomRegion Room(IReadOnlyList<string> wallIds, params string[] evidence) =>
        new(
            "room-review-supported",
            1,
            new PlanRect(0, 0, 100, 100),
            [
                new PlanPoint(0, 0),
                new PlanPoint(100, 0),
                new PlanPoint(100, 100),
                new PlanPoint(0, 100)
            ],
            wallIds,
            Confidence.High)
        {
            UseKind = RoomUseKind.Office,
            Evidence = evidence
        };

    private static WallSegment Wall(string id, double x1, double y1, double x2, double y2) =>
        new(
            id,
            1,
            new PlanLineSegment(new PlanPoint(x1, y1), new PlanPoint(x2, y2)),
            6,
            Confidence.High)
        {
            WallType = WallType.Interior,
            DetectionKind = WallDetectionKind.ParallelLinePair,
            Evidence = ["parallel wall-face pair", "wall evidence: strong double-edge wall body"]
        };
}
