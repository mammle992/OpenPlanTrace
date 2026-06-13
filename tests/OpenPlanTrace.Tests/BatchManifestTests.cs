using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenPlanTrace.Tests;

public sealed class BatchManifestTests
{
    [Fact]
    public void BatchManifest_DeserializesPlanSetOptions()
    {
        const string json = """
            {
              "schemaVersion": "openplantrace.batch-manifest.v1",
              "name": "Industrial plan set",
              "outputDirectory": "out/plans",
              "summaryJsonPath": "out/plans/batch.json",
              "inputs": ["plans", "sample.pdf"],
              "recursive": true,
              "writeSvg": false,
              "writeGeoJson": true,
              "maxDegreeOfParallelism": 3,
              "retryCount": 2,
              "layerProfiles": ["layers.json"],
              "objectLabelProfiles": ["objects.json"],
              "layerCategoryOverrides": [
                { "pattern": "E-EQP-*", "category": "Equipment", "sourceFormat": "dxf" }
              ],
              "scannerOptions": {
                "minWallLength": 18,
                "maxWallCandidateSeedsPerPage": 20000,
                "wallSnapTolerance": 2.75,
                "objectNearbyTextSearchRadius": 64,
                "maxNearbyTextPerObject": 4
              }
            }
            """;

        var manifest = JsonSerializer.Deserialize<BatchScanManifest>(json, CreateJsonOptions())!;

        BatchScanManifest.ValidateSchemaVersion(manifest);
        Assert.Equal(BatchScanManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Equal("Industrial plan set", manifest.Name);
        Assert.True(manifest.Recursive);
        Assert.False(manifest.WriteSvg);
        Assert.True(manifest.WriteGeoJson);
        Assert.Equal(3, manifest.MaxDegreeOfParallelism);
        Assert.Equal(2, manifest.RetryCount);
        Assert.Equal(new[] { "plans", "sample.pdf" }, manifest.Inputs);
        Assert.Single(manifest.LayerProfiles);
        Assert.Single(manifest.ObjectLabelProfiles);
        Assert.Single(manifest.LayerCategoryOverrides);
        Assert.Equal("E-EQP-*", manifest.LayerCategoryOverrides[0].Pattern);
        Assert.Equal(LayerCategory.Equipment, manifest.LayerCategoryOverrides[0].Category);
        Assert.Equal("dxf", manifest.LayerCategoryOverrides[0].SourceFormat);
        Assert.Equal(18, manifest.ScannerOptions!.MinWallLength);
        Assert.Equal(20000, manifest.ScannerOptions.MaxWallCandidateSeedsPerPage);
        Assert.Equal(2.75, manifest.ScannerOptions.WallSnapTolerance);
        Assert.Equal(64, manifest.ScannerOptions.ObjectNearbyTextSearchRadius);
        Assert.Equal(4, manifest.ScannerOptions.MaxNearbyTextPerObject);
    }

    [Fact]
    public void BatchManifest_RejectsUnsupportedSchemaVersion()
    {
        var manifest = new BatchScanManifest
        {
            SchemaVersion = "openplantrace.batch-manifest.v99",
            Inputs = new[] { "sample.pdf" }
        };

        var exception = Assert.Throws<ArgumentException>(
            () => BatchScanManifest.ValidateSchemaVersion(manifest));

        Assert.Contains(BatchScanManifest.CurrentSchemaVersion, exception.Message);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
