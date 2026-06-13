namespace OpenPlanTrace.Tests;

public sealed class ScanReviewQueueSummaryTests
{
    [Fact]
    public void QueuedWallGraphGapDiagnostics_CapsAndRanksNearestEndpointGaps()
    {
        var gapDiagnostics = Enumerable.Range(0, 36)
            .Select(index => WallGap(
                $"node-{index:00}",
                distance: 60 - index,
                gapKind: index % 2 == 0 ? "EndpointToWall" : "EndpointToEndpoint"))
            .ToArray();
        var unrelated = new PlanDiagnostic(
            "wall_graph.endpoint_gaps.detected",
            DiagnosticSeverity.Warning,
            "wall-graph",
            "summary");

        var queued = ScanReviewQueueSummary.QueuedWallGraphGapDiagnostics(gapDiagnostics.Concat(new[] { unrelated }));

        Assert.Equal(ScanReviewQueueSummary.WallGraphGapReviewQueueLimit, queued.Count);
        Assert.Equal("node-35", queued[0].Properties["nodeId"]);
        Assert.Contains(queued, diagnostic => diagnostic.Properties["nodeId"] == "node-12");
        Assert.DoesNotContain(queued, diagnostic => diagnostic.Properties["nodeId"] == "node-00");
        Assert.DoesNotContain(queued, diagnostic => diagnostic.Code == "wall_graph.endpoint_gaps.detected");
    }

    [Fact]
    public void WallGraphGapReviewReason_DescribesWhyGapWasQueued()
    {
        var diagnostic = WallGap("node-a", 12, "EndpointToWall");

        var reason = ScanReviewQueueSummary.WallGraphGapReviewReason(diagnostic);

        Assert.Contains("EndpointToWall", reason);
        Assert.Contains("gap 12 drawing unit", reason);
        Assert.Contains("walls wall-a,wall-b", reason);
    }

    [Fact]
    public void QueuedObjectGroups_CapsAndRanksActionableReviewGroups()
    {
        var repeatedGroups = Enumerable.Range(0, 30)
            .Select(index => Group(
                $"repeat-{index:00}",
                count: 2 + index % 4,
                sourcePrimitiveIds: new[] { $"src-{index}" }))
            .ToArray();
        var taggedGroup = Group(
            "tagged-top",
            count: 1,
            detectedTags: new[] { "P27" },
            sourcePrimitiveIds: new[] { "tag-src" });
        var lowInformationSingle = Group("low-info-single", count: 1);
        var notReviewRequired = Group("not-review-required", count: 50, requiresReview: false);

        var queued = ScanReviewQueueSummary.QueuedObjectGroups(
            repeatedGroups
                .Concat(new[] { taggedGroup, lowInformationSingle, notReviewRequired }));

        Assert.Equal(ScanReviewQueueSummary.ObjectGroupReviewQueueLimit, queued.Count);
        Assert.Equal("tagged-top", queued[0].Id);
        Assert.Contains(queued, group => group.Id.StartsWith("repeat-", StringComparison.Ordinal));
        Assert.DoesNotContain(queued, group => group.Id == "low-info-single");
        Assert.DoesNotContain(queued, group => group.Id == "not-review-required");
    }

    [Fact]
    public void ObjectGroupReviewReason_DescribesWhyGroupWasQueued()
    {
        var group = Group(
            "contextual-group",
            count: 3,
            detectedTags: new[] { "P27", "VAV-01" },
            nearbyText: new[]
            {
                new ObjectNearbyText(
                    "Terrasse",
                    1,
                    new PlanRect(40, 50, 20, 8),
                    "text-1",
                    12)
            })
            with
            {
                Label = "Equipment",
                SymbolName = "EQUIP_TAG"
            };

        var reason = ScanReviewQueueSummary.ObjectGroupReviewReason(group);

        Assert.Contains("repeated 3 occurrence", reason);
        Assert.Contains("2 detected tag", reason);
        Assert.Contains("1 nearby text", reason);
        Assert.Contains("draft label present", reason);
        Assert.Contains("symbol name present", reason);
    }

    private static PlanDiagnostic WallGap(string nodeId, double distance, string gapKind) =>
        new(
            "wall_graph.endpoint_gap.review",
            DiagnosticSeverity.Warning,
            "wall-graph",
            "A wall graph endpoint nearly touches another wall endpoint or host wall but was not safely snapped.")
        {
            PageNumber = 1,
            Region = new PlanRect(10, 10, 20, 20),
            Confidence = Confidence.Medium,
            SourcePrimitiveIds = new[] { "wall-a-src", "wall-b-src" },
            Properties = new Dictionary<string, string>
            {
                ["gapKind"] = gapKind,
                ["gapDistance"] = distance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                ["nodeId"] = nodeId,
                ["hostWallId"] = "wall-b",
                ["targetNodeId"] = string.Empty,
                ["wallIds"] = "wall-a,wall-b"
            }
        };

    private static ObjectCandidateGroup Group(
        string id,
        int count,
        bool requiresReview = true,
        IReadOnlyList<string>? detectedTags = null,
        IReadOnlyList<ObjectNearbyText>? nearbyText = null,
        IReadOnlyList<string>? sourcePrimitiveIds = null) =>
        new(
            id,
            $"signature:{id}",
            ObjectCandidateKind.Symbol,
            ObjectCategory.GenericSymbol,
            count,
            new PlanRect(10 + count, 20, 8, 8),
            new[] { 1 },
            Enumerable.Range(0, Math.Max(count, 1))
                .Select(index => $"{id}:candidate:{index + 1}")
                .ToArray(),
            sourcePrimitiveIds ?? Array.Empty<string>(),
            requiresReview,
            Confidence.Medium,
            new[] { $"review evidence for {id}" })
        {
            DetectedTags = detectedTags ?? Array.Empty<string>(),
            NearbyText = nearbyText ?? Array.Empty<ObjectNearbyText>()
        };
}
