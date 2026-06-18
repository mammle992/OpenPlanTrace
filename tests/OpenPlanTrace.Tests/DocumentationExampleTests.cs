using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class DocumentationExampleTests
{
    [Fact]
    public void PublicScanExample_UsesCurrentScanSchemaAndPublicFixture()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "examples",
            "openplantrace.scan.example.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetString());
        var sourcePath = root.GetProperty("document").GetProperty("sourcePath").GetString();
        Assert.Equal("samples/golden/semantic-smoke.dxf", sourcePath);
        Assert.DoesNotContain("C:", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.True(root.GetProperty("walls").GetArrayLength() > 0);
        Assert.True(root.GetProperty("rooms").GetArrayLength() > 0);
        Assert.True(root.GetProperty("openings").GetArrayLength() > 0);
    }

    [Fact]
    public void PublicGeoJsonExample_UsesCurrentGeoJsonSchemaAndPageCoordinates()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "examples",
            "openplantrace.geojson.example.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.Equal("FeatureCollection", root.GetProperty("type").GetString());
        Assert.Equal(PlanTraceGeoJsonExporter.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetString());
        Assert.Equal("OpenPlanTracePageCoordinates", root.GetProperty("coordinateSpace").GetString());
        Assert.Equal(
            "samples/golden/semantic-smoke.dxf",
            root.GetProperty("document").GetProperty("sourcePath").GetString());
        Assert.DoesNotContain("C:", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            root.GetProperty("features").EnumerateArray(),
            feature => feature.GetProperty("properties").GetProperty("featureType").GetString() == "wall");
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
