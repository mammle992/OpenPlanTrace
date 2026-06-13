using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenPlanTrace.Tests;

public sealed class GoldenFixtureTests
{
    [Fact]
    public async Task GoldenSemanticSmokeDxf_PassesBenchmarkManifest()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(repositoryRoot, "samples", "golden", "benchmark.json");
        var manifestDirectory = Path.GetDirectoryName(manifestPath)!;
        await using var manifestStream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<BenchmarkManifest>(
            manifestStream,
            CreateJsonOptions());

        Assert.NotNull(manifest);
        var fixture = Assert.Single(manifest!.Fixtures);
        var sourcePath = Path.GetFullPath(Path.Combine(manifestDirectory, fixture.SourcePath));
        var engine = new OpenPlanTraceEngine(
            new PlanDocumentLoaderRegistry(new IPlanDocumentLoader[]
            {
                new IxMiliaDxfPlanDocumentLoader()
            }));

        var result = await engine.ScanFileAsync(sourcePath);
        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.Zero);

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        Assert.True(result.Calibration.HasReliableMeasurementScale);
        Assert.Single(result.Rooms, room => string.Equals(room.Label, "OFFICE", StringComparison.OrdinalIgnoreCase));
        Assert.Single(result.Openings, opening => opening.Type == OpeningType.Door && opening.Operation == OpeningOperation.Hinged);
        Assert.Contains(result.ObjectCandidates, candidate => candidate.Category == ObjectCategory.HVACEquipment);
        Assert.Contains(result.ObjectGroups, group => group.RequiresReview && group.Count >= 2);
    }

    [Fact]
    public void GoldenBenchmarkResultSample_UsesCurrentSchemaAndScoreboardContract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var resultPath = Path.Combine(repositoryRoot, "samples", "golden", "benchmark-output.json");
        using var document = JsonDocument.Parse(File.ReadAllText(resultPath));
        var root = document.RootElement;

        Assert.Equal(BenchmarkRunResult.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetString());
        Assert.True(root.GetProperty("passed").GetBoolean());

        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(cases.Length, root.GetProperty("caseCount").GetInt32());
        Assert.Equal(root.GetProperty("reviewQueue").GetArrayLength(), root.GetProperty("reviewQueueCount").GetInt32());
        Assert.Equal(cases.Count(item => item.GetProperty("passed").GetBoolean()), root.GetProperty("passedCaseCount").GetInt32());
        Assert.Equal(0, root.GetProperty("failedCaseCount").GetInt32());
        Assert.Equal(0, root.GetProperty("skippedCaseCount").GetInt32());

        var scoreboard = root.GetProperty("scoreboard");
        Assert.Equal(BenchmarkScoreboard.CurrentSchemaVersion, scoreboard.GetProperty("schemaVersion").GetString());
        Assert.Equal("Strong", scoreboard.GetProperty("grade").GetString());
        Assert.True(scoreboard.GetProperty("readyForDownstreamUse").GetBoolean());
        Assert.Equal(root.GetProperty("caseCount").GetInt32(), scoreboard.GetProperty("caseCount").GetInt32());
        Assert.Equal(root.GetProperty("failedAssertionCount").GetInt32(), scoreboard.GetProperty("failedAssertionCount").GetInt32());
        Assert.Equal(3, scoreboard.GetProperty("expectedTargetCount").GetInt32());
        Assert.Equal(3, scoreboard.GetProperty("matchedTargetCount").GetInt32());
        Assert.Equal(0, scoreboard.GetProperty("missedTargetCount").GetInt32());
        Assert.Equal(0, scoreboard.GetProperty("extraDetectionCount").GetInt32());
        Assert.NotEmpty(scoreboard.GetProperty("detectors").EnumerateArray());

        var fixture = Assert.Single(cases);
        var counts = fixture.GetProperty("counts");
        foreach (var requiredCurrentCount in new[]
                 {
                     "gridBaySpacings",
                     "surfacePatterns",
                     "objectAggregates",
                     "routingItems",
                     "routingSuppressedObjects",
                     "measurementCheckedCount",
                     "measurementConsistentCount",
                     "measurementOutlierCount",
                     "measurementConsistencyConfidence"
                 })
        {
            Assert.True(counts.TryGetProperty(requiredCurrentCount, out _), $"Golden benchmark sample is missing count '{requiredCurrentCount}'.");
        }

        Assert.NotEmpty(fixture.GetProperty("metrics").EnumerateArray());
        foreach (var metric in fixture.GetProperty("metrics").EnumerateArray())
        {
            Assert.True(metric.TryGetProperty("extraDetections", out var extras), "Golden benchmark metric is missing extraDetections.");
            Assert.Equal(metric.GetProperty("extraCount").GetInt32(), extras.GetArrayLength());
        }

        Assert.NotEmpty(fixture.GetProperty("qualityIssues").EnumerateArray());
        Assert.NotEmpty(fixture.GetProperty("diagnosticIssues").EnumerateArray());
        Assert.NotEmpty(fixture.GetProperty("stages").EnumerateArray());
    }

    [Fact]
    public void GoldenBenchmarkComparisonSample_UsesCurrentSchemaAndCurrentCountDeltas()
    {
        var repositoryRoot = FindRepositoryRoot();
        var comparisonPath = Path.Combine(repositoryRoot, "samples", "golden", "benchmark-comparison.json");
        using var document = JsonDocument.Parse(File.ReadAllText(comparisonPath));
        var root = document.RootElement;

        Assert.Equal(BenchmarkComparisonResult.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetString());
        Assert.True(root.GetProperty("passed").GetBoolean());
        Assert.Equal(1, root.GetProperty("matchedCaseCount").GetInt32());
        Assert.Equal(0, root.GetProperty("regressionCount").GetInt32());
        Assert.Equal(0, root.GetProperty("improvementCount").GetInt32());
        Assert.Empty(root.GetProperty("signals").EnumerateArray());

        var fixture = Assert.Single(root.GetProperty("cases").EnumerateArray());
        Assert.Equal(BenchmarkComparisonCaseStatus.Matched.ToString(), fixture.GetProperty("status").GetString());

        var countDeltaNames = fixture
            .GetProperty("countDeltas")
            .EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .ToArray();
        foreach (var currentDelta in new[]
                 {
                     "gridBaySpacings",
                     "surfacePatterns",
                     "objectAggregates",
                     "routingItems",
                     "routingSuppressedObjects",
                     "measurementChecked",
                     "measurementOutliers"
                 })
        {
            Assert.Contains(currentDelta, countDeltaNames);
        }
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
