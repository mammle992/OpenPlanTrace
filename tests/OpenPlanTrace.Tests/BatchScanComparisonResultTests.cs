using OpenPlanTrace.Export;

namespace OpenPlanTrace.Tests;

public sealed class BatchScanComparisonResultTests
{
    [Fact]
    public void Compare_PreservesReviewArtifactLinksAndFlagsLargeCountDrift()
    {
        var baseline = CreateRun(
            "baseline",
            CreateItem(
                walls: 106,
                rooms: 9,
                openings: 8,
                objects: 12,
                objectAggregates: 3,
                visualDrawableItems: 816,
                durationMilliseconds: 1200,
                scanJsonPath: @"C:\runs\baseline\scan.json",
                visualSnapshotPath: @"C:\runs\baseline\visual-snapshot.json",
                geoJsonPath: @"C:\runs\baseline\scan.geojson",
                placementJsonPath: @"C:\runs\baseline\placement.json",
                overlayDirectory: @"C:\runs\baseline\overlays"));
        var candidate = CreateRun(
            "candidate",
            CreateItem(
                walls: 39,
                rooms: 1,
                openings: 4,
                objects: 9,
                objectAggregates: 2,
                visualDrawableItems: 500,
                durationMilliseconds: 1180,
                scanJsonPath: @"C:\runs\candidate\scan.json",
                visualSnapshotPath: @"C:\runs\candidate\visual-snapshot.json",
                geoJsonPath: @"C:\runs\candidate\scan.geojson",
                placementJsonPath: @"C:\runs\candidate\placement.json",
                overlayDirectory: @"C:\runs\candidate\overlays"));

        var comparison = BatchScanComparisonResult.Compare(baseline, candidate);

        Assert.Equal(BatchScanComparisonResult.CurrentSchemaVersion, comparison.SchemaVersion);
        Assert.True(comparison.Passed);
        Assert.Equal(1, comparison.MatchedItemCount);
        Assert.Equal(0, comparison.RegressionCount);
        Assert.Equal(3, comparison.InfoCount);

        var item = Assert.Single(comparison.Items);
        Assert.Equal(BatchScanComparisonItemStatus.Matched, item.Status);
        Assert.Equal(@"C:\runs\baseline\scan.json", item.BaselineScanJsonPath);
        Assert.Equal(@"C:\runs\candidate\visual-snapshot.json", item.CandidateVisualSnapshotPath);
        Assert.Equal(@"C:\runs\baseline\scan.geojson", item.BaselineGeoJsonPath);
        Assert.Equal(@"C:\runs\candidate\placement.json", item.CandidatePlacementJsonPath);
        Assert.Equal(@"C:\runs\candidate\overlays", item.CandidateOverlayDirectory);
        Assert.Contains(item.Deltas, delta => delta.Name == "walls" && delta.Delta == -67);
        Assert.Contains(item.Deltas, delta => delta.Name == "rooms" && delta.Delta == -8);

        var signalCodes = comparison.Signals.Select(signal => signal.Code).ToArray();
        Assert.Contains("counts.walls_changed", signalCodes);
        Assert.Contains("counts.rooms_changed", signalCodes);
        Assert.Contains("counts.visualDrawableItems_changed", signalCodes);
    }

    [Fact]
    public void Compare_TreatsRemovedItemAsRegression()
    {
        var baseline = CreateRun("baseline", CreateItem());
        var candidate = CreateRun("candidate");

        var comparison = BatchScanComparisonResult.Compare(baseline, candidate);

        Assert.False(comparison.Passed);
        Assert.Equal(0, comparison.MatchedItemCount);
        Assert.Equal(1, comparison.RemovedItemCount);
        Assert.Equal(1, comparison.RegressionCount);

        var item = Assert.Single(comparison.Items);
        Assert.Equal(BatchScanComparisonItemStatus.Removed, item.Status);
        Assert.Equal("Blyverket A20-1 Plan 1. etasje.pdf", item.Key);
        Assert.Null(item.CandidateInputPath);
        Assert.Equal("item.removed", Assert.Single(item.Signals).Code);
    }

    [Fact]
    public void MarkdownReport_IncludesEvidenceColumnAndArtifactAvailability()
    {
        var baseline = CreateRun(
            "baseline",
            CreateItem(
                scanJsonPath: @"C:\runs\baseline\scan.json",
                visualSnapshotPath: @"C:\runs\baseline\visual-snapshot.json",
                geoJsonPath: @"C:\runs\baseline\scan.geojson",
                placementJsonPath: @"C:\runs\baseline\placement.json",
                overlayDirectory: @"C:\runs\baseline\overlays"));
        var candidate = CreateRun(
            "candidate",
            CreateItem(
                scanJsonPath: @"C:\runs\candidate\scan.json",
                visualSnapshotPath: @"C:\runs\candidate\visual-snapshot.json",
                geoJsonPath: @"C:\runs\candidate\scan.geojson",
                placementJsonPath: @"C:\runs\candidate\placement.json",
                overlayDirectory: @"C:\runs\candidate\overlays"));

        var markdown = BatchScanComparisonMarkdownReport.Create(
            BatchScanComparisonResult.Compare(baseline, candidate));

        Assert.Contains("| Item | Match | Scan Status | Quality | Diagnostics | Visual Issues | Duration | Evidence | Key Deltas |", markdown);
        Assert.Contains("scan+visual+geojson+placement+svg -> scan+visual+geojson+placement+svg", markdown);
    }

    private static BatchScanRunResult CreateRun(
        string outputDirectoryName,
        params BatchScanItemResult[] items) =>
        new(
            BatchScanRunResult.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            Path.Combine(@"C:\runs", outputDirectoryName),
            1,
            0,
            items);

    private static BatchScanItemResult CreateItem(
        int walls = 106,
        int rooms = 9,
        int openings = 8,
        int objects = 12,
        int objectAggregates = 3,
        int visualDrawableItems = 816,
        double durationMilliseconds = 1200,
        string? scanJsonPath = null,
        string? visualSnapshotPath = null,
        string? geoJsonPath = null,
        string? placementJsonPath = null,
        string? overlayDirectory = null) =>
        new(
            ItemNumber: 1,
            InputPath: @"C:\plans\Blyverket A20-1 Plan 1. etasje.pdf",
            FileName: "Blyverket A20-1 Plan 1. etasje.pdf",
            SourceKind: PlanSourceKind.Pdf,
            EffectiveSourceKind: PlanSourceKind.Pdf,
            Status: BatchScanItemStatus.Succeeded,
            AttemptCount: 1,
            DurationMilliseconds: durationMilliseconds,
            Counts: new BatchScanCounts(
                Pages: 1,
                Regions: 2,
                TitleBlocks: 1,
                Dimensions: 6,
                Annotations: 2,
                GridAxes: 0,
                GridBaySpacings: 0,
                SurfacePatterns: 0,
                Walls: walls,
                WallNodes: 24,
                WallEdges: 22,
                Rooms: rooms,
                RoomAdjacencies: 3,
                RoomClusters: 1,
                Openings: openings,
                Objects: objects,
                ObjectGroups: 2,
                ObjectAggregates: objectAggregates,
                RoutingItems: 18,
                Diagnostics: 0,
                DiagnosticWarnings: 0,
                DiagnosticErrors: 0,
                QualityGrade: "Usable",
                QualityConfidence: 0.804,
                RequiresReview: true),
            ScanJsonPath: scanJsonPath,
            GeoJsonPath: geoJsonPath,
            PlacementJsonPath: placementJsonPath,
            OverlayDirectory: overlayDirectory,
            VisualSnapshotPath: visualSnapshotPath,
            VisualSnapshot: new BatchVisualSnapshotSummary(
                PlanOverlaySnapshot.CurrentSchemaVersion,
                PageCount: 1,
                LayerCount: 12,
                DrawableItemCount: visualDrawableItems,
                IssueCount: 1,
                WarningIssueCount: 1,
                ErrorIssueCount: 0,
                MaxDetectionCoverage: 0.83,
                IssueCodes: new[] { "visual.overlay_coverage_high" }),
            ErrorMessage: null,
            SourceCapability: null);
}
