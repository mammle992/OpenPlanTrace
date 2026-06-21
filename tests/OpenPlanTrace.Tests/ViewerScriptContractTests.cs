namespace OpenPlanTrace.Tests;

public sealed class ViewerScriptContractTests
{
    [Fact]
    public void ViewerWalls_TreatTopologyImportBlockedRepairsAsCoordinateBlocked()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "OpenPlanTrace.Viewer",
            "wwwroot",
            "app.js"));

        Assert.Contains("function wallHasTopologyImportBlockedRepair", script);
        Assert.Contains("wallHasTopologyImportBlockedRepair(wall)", script);
        Assert.Contains("wallGraphRepairCandidates", script);
        Assert.Contains("topologyimportblocked", script);
        Assert.Contains("function wallGraphRepairCoordinateImpactsWall", script);
        Assert.Contains("function wallGraphRepairIsEndpointToWallHost", script);
        Assert.Contains("wallGraphRepairIsEndpointToWallHost(candidate, wallId)", script);
        Assert.Contains("kind === \"endpointtowall\" && hostWallId === String(wallId)", script);
        Assert.Contains("wallGraphRepairCoordinateImpactsWall(candidate, wallId)", script);
    }

    [Fact]
    public void ViewerWalls_NormalizeOffAxisScanTopologySpansBeforeDrawing()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "OpenPlanTrace.Viewer",
            "wwwroot",
            "app.js"));

        Assert.Contains("function normalizeViewerWallTopologySpan", script);
        Assert.Contains("dominantOrthogonalLineOrientation(wallLine)", script);
        Assert.Contains("viewerSpanLeavesSourceAxis(spanLine, orientation)", script);
        Assert.Contains("viewer wall-span cleanup: projected graph span back to source wall axis", script);
        Assert.Contains("wall.topologySpans", script);
        Assert.Contains(".filter(Boolean)", script);
        Assert.Contains("function mergeViewerCleanTopologyRuns", script);
        Assert.Contains("viewer clean placement run merged", script);
        Assert.Contains("viewerCleanWallMergeGap", script);
    }

    [Fact]
    public void ViewerWalls_FilterMicroTopologyRunsBeforeWallQaDrawing()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "OpenPlanTrace.Viewer",
            "wwwroot",
            "app.js"));

        Assert.Contains("const viewerCleanWallMinSpanLength = 8.0", script);
        Assert.Contains("function viewerSpanLength", script);
        Assert.Contains(".filter((interval) => interval.length >= viewerCleanWallMinSpanLength)", script);
        Assert.Contains("if (!intervals.length)", script);
        Assert.Contains("return [];", script);
        Assert.DoesNotContain("if (intervals.length <= 1) {\r\n    return spans;", script);
        Assert.DoesNotContain("if (intervals.length <= 1) {\n    return spans;", script);
    }

    [Fact]
    public void ViewerLayers_ExposeWallQaPresetForCleanWallScreenshots()
    {
        var repositoryRoot = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "OpenPlanTrace.Viewer",
            "wwwroot",
            "index.html"));
        var script = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "OpenPlanTrace.Viewer",
            "wwwroot",
            "app.js"));

        Assert.Contains("id=\"applyWallQaLayers\"", html);
        Assert.Contains(">WALL QA<", html);
        Assert.Contains("const wallQaEnabledLayers", script);
        Assert.Contains("\"wallTopologySpans\"", script);
        Assert.Contains("applyOverlayLayerPreset(wallQaEnabledLayers)", script);
        Assert.Contains("function applyOverlayLayerPreset", script);
        Assert.Contains("state.enabledLayers = new Set(layerKeys)", script);
    }

    [Fact]
    public void ViewerWalls_UseTopLevelPlacementReadinessForCleanWallDrawing()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "OpenPlanTrace.Viewer",
            "wwwroot",
            "app.js"));

        Assert.Contains("wall?.readyForCoordinatePlacement === false || wall?.requiresReview === true", script);
        Assert.Contains("wall?.readyForCoordinatePlacement === false", script);
        Assert.Contains("function wallIsPlacementReady", script);
        Assert.Contains("function wallCoordinateBlocked", script);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenPlanTrace.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate OpenPlanTrace repository root.");
    }
}
