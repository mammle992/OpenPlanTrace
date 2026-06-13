public sealed class BenchmarkManifestDraftMarkdownReportTests
{
    [Fact]
    public void Create_SummarizesDraftTargetsAndReviewRisks()
    {
        var manifest = new BenchmarkManifest
        {
            Name = "Provided PDF draft",
            Fixtures = new[]
            {
                new BenchmarkFixture
                {
                    Id = "sample-plan",
                    Name = "Sample Plan",
                    SourcePath = "%USERPROFILE%\\Downloads\\sample.pdf",
                    Optional = true,
                    SkipReason = "Local sample.",
                    Properties = new Dictionary<string, string>
                    {
                        ["draftedFrom"] = "scan-json",
                        ["sourceName"] = "sample.pdf"
                    },
                    Expectations = new BenchmarkExpectations
                    {
                        MinPages = 1,
                        MinWalls = 4,
                        MinRooms = 2,
                        MaxDiagnosticErrors = 0,
                        MinQualityGrade = PlanScanQualityGrade.ReviewRequired,
                        RegionMetrics = new BenchmarkDetectorMetricExpectations
                        {
                            MinRecall = 1.0,
                            Targets = new[]
                            {
                                new BenchmarkDetectionTarget
                                {
                                    Id = "region:main",
                                    PageNumber = 1,
                                    Bounds = new PlanRect(10, 20, 300, 200),
                                    RegionKind = RegionKind.MainFloorPlan,
                                    Confidence = 0.88,
                                    SourcePrimitiveIds = new[] { "region-source" },
                                    SourceLayers = new[] { "A-FLOR" },
                                    Evidence = new[] { "largest dense drawing region" }
                                }
                            }
                        },
                        RoomMetrics = new BenchmarkDetectorMetricExpectations
                        {
                            MinRecall = 1.0,
                            Targets = new[]
                            {
                                new BenchmarkDetectionTarget
                                {
                                    Id = "room:low",
                                    PageNumber = 1,
                                    Label = "PUMP",
                                    Confidence = 0.42
                                }
                            }
                        },
                        ObjectMetrics = new BenchmarkDetectorMetricExpectations
                        {
                            Targets = new[]
                            {
                                new BenchmarkDetectionTarget
                                {
                                    Id = "object:no-provenance",
                                    PageNumber = 1,
                                    ObjectCategory = ObjectCategory.GenericSymbol,
                                    DetectedTags = new[] { "P-101" }
                                }
                            }
                        }
                    }
                }
            }
        };

        var markdown = BenchmarkManifestDraftMarkdownReport.Create(manifest);

        Assert.Contains("# Benchmark Draft Review", markdown);
        Assert.Contains("Provided PDF draft", markdown);
        Assert.Contains("| Sample Plan | %USERPROFILE%\\Downloads\\sample.pdf | 3 | 2 | 1 | 1 |", markdown);
        Assert.Contains("- Count gates: pages >= 1, walls >= 4, rooms >= 2", markdown);
        Assert.Contains("| regions | 1 | 1 | - | 1 | 1 | 0 |", markdown);
        Assert.Contains("| rooms | 1 | 1 | - | 0 | 1 | 1 |", markdown);
        Assert.Contains("| objects | 1 | - | - | 0 | 0 | 0 |", markdown);
        Assert.Contains("`region:main`: page 1; bounds (10, 20, 300, 200); region `MainFloorPlan`", markdown);
        Assert.Contains("`object:no-provenance`: page 1; object `GenericSymbol`; detected tags P-101", markdown);
        Assert.Contains("Source layers: A-FLOR", markdown);
        Assert.Contains("Evidence: largest dense drawing region", markdown);
        Assert.Contains("`room:low`: page 1; label `PUMP`", markdown);
        Assert.Contains("Confidence: 0.42", markdown);
    }

    [Fact]
    public void Create_HandlesEmptyManifest()
    {
        var markdown = BenchmarkManifestDraftMarkdownReport.Create(new BenchmarkManifest());

        Assert.Contains("No fixtures in this draft.", markdown);
        Assert.Contains("Fixtures: 0", markdown);
    }
}
