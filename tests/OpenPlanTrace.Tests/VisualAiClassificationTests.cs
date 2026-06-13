using System.Text.Json;
using OpenPlanTrace.Export;

namespace OpenPlanTrace.Tests;

public sealed class VisualAiClassificationTests
{
    [Fact]
    public async Task ScanAsync_WhenVisualAiRequestedWithoutClassifier_ReportsMissingClassifier()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            CreateRepeatedSymbolDocument(),
            new ScannerOptions { EnableVisualAiClassification = true });

        Assert.Contains(
            result.Diagnostics.Messages,
            message => message.Code == "visual_ai.classifier_missing");
        Assert.All(result.ObjectCandidates, candidate => Assert.Null(candidate.VisualAi));
        Assert.All(result.ObjectGroups, group => Assert.Null(group.VisualAi));
    }

    [Fact]
    public async Task ScanAsync_WhenKvemoCropSinkConfiguredWithoutClassifier_ExportsCropsWithoutLabels()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openplantrace-kvemo-{Guid.NewGuid():N}");
        try
        {
            var result = await new OpenPlanTraceScanner().ScanAsync(
                CreateRepeatedSymbolDocument(),
                new ScannerOptions
                {
                    EnableVisualAiClassification = true,
                    VisualAiCropProvider = new PrimitiveVectorVisualAiCropProvider(64, 64),
                    VisualAiCropSink = new DirectoryVisualAiCropSink(directory),
                    MaxVisualAiCropsPerScan = 3,
                    VisualAiCropPadding = 8
                });

            Assert.Contains(
                result.Diagnostics.Messages,
                message => message.Code == "kvemo.crop_export_only");
            Assert.Contains(
                result.Diagnostics.Messages,
                message => message.Code == "kvemo.crops.exported");
            Assert.All(result.ObjectCandidates, candidate => Assert.Null(candidate.VisualAi));
            Assert.All(result.ObjectGroups, group => Assert.Null(group.VisualAi));

            var pngs = Directory.GetFiles(directory, "*.png");
            Assert.NotEmpty(pngs);
            var firstBytes = await File.ReadAllBytesAsync(pngs[0]);
            Assert.Equal(new byte[] { 137, 80, 78, 71 }, firstBytes.Take(4).ToArray());

            var manifestPath = Path.Combine(directory, "kvemo-crops.jsonl");
            Assert.True(File.Exists(manifestPath));
            var line = Assert.Single((await File.ReadAllLinesAsync(manifestPath)).Take(1));
            using var parsed = JsonDocument.Parse(line);
            Assert.Equal("openplantrace.kvemo-crops.v2", parsed.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal("Kvemo", parsed.RootElement.GetProperty("engine").GetString());
            Assert.Equal("visual-ai-symbols", parsed.RootElement.GetProperty("documentId").GetString());
            Assert.Equal(
                "symbol:iso_tag_71|category:Equipment|kind:Symbol|layers:x-symbols",
                parsed.RootElement.GetProperty("reviewKey").GetString());
            Assert.Equal(
                "symbol:iso_tag_71|category:Equipment|kind:Symbol|layers:x-symbols",
                parsed.RootElement.GetProperty("groupSignature").GetString());
            Assert.Equal("page", parsed.RootElement.GetProperty("coordinateSpace").GetString());
            Assert.Equal("top-left", parsed.RootElement.GetProperty("coordinateOrigin").GetString());
            Assert.Equal("down", parsed.RootElement.GetProperty("coordinateYAxisDirection").GetString());
            Assert.Equal(700, parsed.RootElement.GetProperty("pageWidth").GetDouble());
            Assert.Equal(500, parsed.RootElement.GetProperty("pageHeight").GetDouble());
            Assert.Equal("CadSymbol", parsed.RootElement.GetProperty("sourceKind").GetString());
            Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("sourceWallComponentId").ValueKind);
            Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("sourceWallComponentKind").ValueKind);
            Assert.Contains(
                parsed.RootElement.GetProperty("sourceKindCounts").EnumerateArray(),
                item => item.GetProperty("value").GetString() == "CadSymbol" && item.GetProperty("count").GetInt32() > 0);
            Assert.Empty(parsed.RootElement.GetProperty("sourceWallComponentIds").EnumerateArray());
            Assert.Empty(parsed.RootElement.GetProperty("sourceWallComponentKindCounts").EnumerateArray());
            Assert.True(parsed.RootElement.GetProperty("deterministicConfidence").GetDouble() > 0);
            Assert.True(parsed.RootElement.GetProperty("objectToCropAreaRatio").GetDouble() > 0);
            Assert.Equal("High", parsed.RootElement.GetProperty("reviewPriority").GetString());
            Assert.Equal("symbol-labeling-candidate", parsed.RootElement.GetProperty("suggestedTrainingUse").GetString());
            var visualSimilarityKey = parsed.RootElement.GetProperty("visualSimilarityKey").GetString();
            Assert.StartsWith("kvemo:", visualSimilarityKey);
            var fingerprint = parsed.RootElement.GetProperty("imageFingerprint");
            Assert.Equal(visualSimilarityKey, fingerprint.GetProperty("similarityKey").GetString());
            Assert.Matches("^[0-9a-f]{16}$", fingerprint.GetProperty("averageHash64").GetString());
            Assert.Matches("^[0-9a-f]{16}$", fingerprint.GetProperty("differenceHash64").GetString());
            Assert.True(fingerprint.GetProperty("inkRatio").GetDouble() >= 0);
            Assert.True(fingerprint.GetProperty("objectAspectRatio").GetDouble() > 0);
            Assert.Contains(
                parsed.RootElement.GetProperty("reviewReasons").EnumerateArray(),
                item => item.GetString() == "has CAD symbol/block name evidence");
            Assert.NotEmpty(parsed.RootElement.GetProperty("evidence").EnumerateArray());
            Assert.True(parsed.RootElement.GetProperty("imageWidth").GetInt32() > 0);
            var sourceEvidence = parsed.RootElement.GetProperty("sourceEvidence");
            Assert.True(sourceEvidence.GetProperty("primitiveCount").GetInt32() > 0);
            Assert.Contains(
                sourceEvidence.GetProperty("layers").EnumerateArray(),
                item => item.GetString() == "X-SYMBOLS");
            Assert.Contains(
                sourceEvidence.GetProperty("entityTypes").EnumerateArray(),
                item => item.GetString() == "INSERT");
            Assert.Contains(
                sourceEvidence.GetProperty("blockNames").EnumerateArray(),
                item => item.GetString() == "ISO_TAG_71");
            Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("classification").ValueKind);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void KvemoCropManifestEntry_UsesVisualSimilarityKeyForUnlabeledUngroupedCrops()
    {
        var document = new PlanDocument(
            "kvemo-fingerprint-review-key",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(120, 90),
                    Array.Empty<PlanPrimitive>())
            });
        var pixels = Enumerable.Repeat((byte)255, 16 * 16 * 3).ToArray();
        for (var index = 0; index < 16; index++)
        {
            var offset = ((index * 16) + index) * 3;
            pixels[offset] = 20;
            pixels[offset + 1] = 24;
            pixels[offset + 2] = 28;
        }

        var artifact = new VisualAiCropArtifact(
            "object:unlabeled-1",
            "object",
            null,
            1,
            new PlanRect(40, 20, 18, 12),
            new PlanRect(34, 14, 30, 24),
            ObjectCandidateKind.Unknown,
            ObjectCategory.Unknown,
            ObjectCandidateSourceKind.Unknown,
            null,
            null,
            0.27,
            null,
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new[] { "unknown-source-1" },
            new[] { "unlabeled crop exported for grouping review" },
            VisualAiImage.Rgb(16, 16, pixels, "test-crop"),
            null);

        var entry = VisualAiCropManifestEntry.From(
            document,
            artifact,
            Path.Combine(Path.GetTempPath(), "kvemo-unlabeled.png"),
            "kvemo-unlabeled.png");

        Assert.StartsWith("kvemo:", entry.VisualSimilarityKey);
        Assert.Equal(entry.VisualSimilarityKey, entry.ImageFingerprint.SimilarityKey);
        Assert.Equal($"visual:{entry.VisualSimilarityKey}", entry.ReviewKey);
        Assert.Contains(entry.SourceKindCounts, item => item.Value == "Unknown" && item.Count == 1);
        Assert.Empty(entry.SourceWallComponentIds);
        Assert.Empty(entry.SourceWallComponentKindCounts);
        Assert.Equal("horizontal", entry.ImageFingerprint.AspectBucket);
        Assert.True(entry.ImageFingerprint.InkRatio > 0);
    }

    [Fact]
    public async Task KvemoCropManifestLabelProfileBuilder_DraftsReusableProfileRulesFromCropManifest()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openplantrace-kvemo-profile-{Guid.NewGuid():N}");
        try
        {
            await new OpenPlanTraceScanner().ScanAsync(
                CreateRepeatedSymbolDocument(),
                new ScannerOptions
                {
                    EnableVisualAiClassification = true,
                    VisualAiCropProvider = new PrimitiveVectorVisualAiCropProvider(64, 64),
                    VisualAiCropSink = new DirectoryVisualAiCropSink(directory),
                    MaxVisualAiCropsPerScan = 1,
                    VisualAiCropPadding = 8
                });

            var manifestPath = Path.Combine(directory, "kvemo-crops.jsonl");
            var result = await KvemoCropManifestLabelProfileBuilder.ReadAsync(
                manifestPath,
                new KvemoCropManifestLabelProfileOptions
                {
                    Name = "Kvemo profile draft",
                    Version = "test"
                });

            Assert.Equal(0, result.InvalidEntryCount);
            Assert.Equal(1, result.EntryCount);
            Assert.Equal(1, result.RuleCount);
            Assert.Equal("Kvemo profile draft", result.Profile.Name);
            Assert.Equal("test", result.Profile.Version);

            var rule = Assert.Single(result.Profile.Rules);
            Assert.Equal("symbol:iso_tag_71|category:Equipment|kind:Symbol|layers:x-symbols", rule.Signature);
            Assert.Equal("P-*", rule.DetectedTagPattern);
            Assert.Equal(ObjectCategory.Equipment, rule.Category);
            Assert.Equal(ObjectCandidateKind.Symbol, rule.Kind);
            Assert.True(rule.RequiresReview);
            Assert.Contains(rule.Evidence, item => item.Contains("Drafted from Kvemo crop manifest", StringComparison.Ordinal));
            Assert.Contains(rule.Evidence, item => item.Contains("Source kinds: CadSymbol:", StringComparison.Ordinal));
            Assert.Contains(rule.Evidence, item => item.Contains("Edit this rule", StringComparison.Ordinal));

            var json = ObjectLabelProfileJsonSerializer.Serialize(result.Profile, writeIndented: false);
            var roundTripped = ObjectLabelProfile.ParseJson(json);
            Assert.Single(roundTripped.Rules);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task KvemoCropManifestReport_SummarizesSourceProvenance()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openplantrace-kvemo-report-{Guid.NewGuid():N}");
        try
        {
            await new OpenPlanTraceScanner().ScanAsync(
                CreateRepeatedSymbolDocument(),
                new ScannerOptions
                {
                    EnableVisualAiClassification = true,
                    VisualAiCropProvider = new PrimitiveVectorVisualAiCropProvider(64, 64),
                    VisualAiCropSink = new DirectoryVisualAiCropSink(directory),
                    MaxVisualAiCropsPerScan = 1,
                    VisualAiCropPadding = 8
                });

            var report = await KvemoCropManifestReport.ReadAsync(Path.Combine(directory, "kvemo-crops.jsonl"));

            Assert.Equal(1, report.EntryCount);
            Assert.Contains(report.BySourceKind, item => item.Value == "CadSymbol" && item.Count > 0);
            Assert.Contains(report.BySourceWallComponentKind, item => item.Value == "(none)" && item.Count == 1);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ScanAsync_AppliesModelBackedVisualAiToRepeatedObjectGroups()
    {
        var classifier = new TestVisualAiClassifier(
            new VisualAiClassificationResult(
                "plan-symbol-test-model",
                "2026.06",
                "test-classifier",
                new VisualAiClassificationCandidate(
                    "process pump",
                    ObjectCategory.Equipment,
                    0.91,
                    new Dictionary<string, string> { ["classIndex"] = "7" }),
                new[]
                {
                    new VisualAiClassificationCandidate(
                        "process pump",
                        ObjectCategory.Equipment,
                        0.91,
                        new Dictionary<string, string> { ["classIndex"] = "7" }),
                    new VisualAiClassificationCandidate(
                        "valve",
                        ObjectCategory.Equipment,
                        0.06,
                        new Dictionary<string, string> { ["classIndex"] = "8" })
                },
                new[]
                {
                    "test classifier received a vector crop",
                    "nearby text and source primitive ids were available"
                }));

        var result = await new OpenPlanTraceScanner().ScanAsync(
            CreateRepeatedSymbolDocument(),
            new ScannerOptions
            {
                EnableVisualAiClassification = true,
                VisualAiClassifier = classifier,
                VisualAiCropProvider = new PrimitiveVectorVisualAiCropProvider(96, 96),
                MaxVisualAiCropsPerScan = 4,
                MinVisualAiConfidence = 0.5,
                VisualAiCropPadding = 10
            });

        var group = Assert.Single(result.ObjectGroups, item => item.SymbolName == "ISO_TAG_71");
        Assert.NotNull(group.VisualAi);
        Assert.Equal("process pump", group.Label);
        Assert.Equal(ObjectCategory.Equipment, group.Category);
        Assert.False(group.RequiresReview);
        Assert.Contains(group.Evidence, item => item.Contains("visual AI classified", StringComparison.OrdinalIgnoreCase));

        var repeatedCandidates = result.ObjectCandidates
            .Where(candidate => candidate.SymbolName == "ISO_TAG_71")
            .OrderBy(candidate => candidate.Bounds.Left)
            .ToArray();
        Assert.Equal(2, repeatedCandidates.Length);
        Assert.All(repeatedCandidates, candidate =>
        {
            Assert.NotNull(candidate.VisualAi);
            Assert.Equal("process pump", candidate.Label);
            Assert.Equal(ObjectCategory.Equipment, candidate.Category);
        });

        Assert.Contains(
            result.Diagnostics.Messages,
            message => message.Code == "visual_ai.classifications.detected");
        Assert.True(classifier.Requests.Count >= 1);
        Assert.Contains(classifier.Requests, request => request.DetectionKind == "object-group");
    }

    [Fact]
    public async Task ScanAsync_KvemoUsesAdaptivePaddingForTinySymbols()
    {
        var classifier = new TestVisualAiClassifier(
            new VisualAiClassificationResult(
                "crop-test",
                "1",
                "test-classifier",
                new VisualAiClassificationCandidate("unknown symbol", ObjectCategory.Unknown, 0.4, new Dictionary<string, string>()),
                Array.Empty<VisualAiClassificationCandidate>(),
                Array.Empty<string>()));

        await new OpenPlanTraceScanner().ScanAsync(
            CreateTinySymbolDocument(),
            new ScannerOptions
            {
                EnableVisualAiClassification = true,
                VisualAiClassifier = classifier,
                VisualAiCropProvider = new PrimitiveVectorVisualAiCropProvider(64, 64),
                VisualAiCropPadding = 18,
                MaxVisualAiCropsPerScan = 1
            });

        var request = Assert.Single(classifier.Requests);
        Assert.True(request.CropBounds.Width <= 24);
        Assert.True(request.CropBounds.Height <= 24);
        Assert.True(request.CropBounds.Width > request.Bounds.Width);
        Assert.True(request.CropBounds.Height > request.Bounds.Height);
    }

    [Fact]
    public void KvemoCropManifestSchema_DocumentsCoordinateProvenanceAndReviewFields()
    {
        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "schemas",
            $"{VisualAiCropManifestEntry.CurrentSchemaVersion}.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing Kvemo crop manifest schema: {schemaPath}");

        using var parsed = JsonDocument.Parse(File.ReadAllText(schemaPath));
        Assert.Equal("urn:openplantrace:schema:kvemo-crops:v2", parsed.RootElement.GetProperty("$id").GetString());
        Assert.Equal(
            VisualAiCropManifestEntry.CurrentSchemaVersion,
            parsed.RootElement.GetProperty("x-openplantrace-schemaVersion").GetString());

        var required = parsed.RootElement
            .GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        foreach (var property in new[]
                 {
                     "reviewKey",
                     "pageWidth",
                     "pageHeight",
                     "coordinateSpace",
                     "coordinateOrigin",
                     "coordinateYAxisDirection",
                     "sourceKind",
                     "sourceWallComponentId",
                     "sourceWallComponentKind",
                     "sourceEvidence",
                     "reviewPriority",
                     "reviewReasons",
                     "suggestedTrainingUse",
                     "visualSimilarityKey",
                     "imageFingerprint"
                 })
        {
            Assert.Contains(property, required);
        }

        var properties = parsed.RootElement.GetProperty("properties");
        Assert.True(properties.TryGetProperty("visualSimilarityKey", out _));
        Assert.True(properties.TryGetProperty("imageFingerprint", out _));
        Assert.True(properties.TryGetProperty("sourceKindCounts", out _));
        Assert.True(properties.TryGetProperty("sourceWallComponentIds", out _));
        Assert.True(properties.TryGetProperty("sourceWallComponentKindCounts", out _));
    }

    [Fact]
    public void KvemoReviewSummary_TreatsDenseRepeatedLineworkGroupsAsSymbolLabelingCandidates()
    {
        var artifact = new VisualAiCropArtifact(
            "object-group:chair",
            "object-group",
            "symbol:chair|category:GenericSymbol|kind:Symbol|layers:x-symbols",
            1,
            new PlanRect(100, 100, 40, 30),
            new PlanRect(94, 94, 52, 42),
            ObjectCandidateKind.Symbol,
            ObjectCategory.GenericSymbol,
            ObjectCandidateSourceKind.CompositeLinework,
            null,
            null,
            0.48,
            null,
            null,
            Array.Empty<string>(),
            new[] { "CHAIR" },
            Enumerable.Range(0, 75).Select(index => $"pdf:p1:path:{index}:subpath:1:polyline").ToArray(),
            new[] { "grouped 2 object candidate(s) by geometry size bucket" },
            VisualAiImage.Rgb(8, 8, Enumerable.Repeat((byte)255, 8 * 8 * 3).ToArray()),
            null);
        var sourceEvidence = new VisualAiSourceEvidenceManifestEntry(
            75,
            75,
            artifact.SourcePrimitiveIds.Take(32).ToArray(),
            Array.Empty<string>(),
            new[] { "pdf" },
            Array.Empty<string>(),
            new[] { "polyline" },
            Array.Empty<string>(),
            new[] { "RGB: (0, 0, 0)" },
            new[] { "solid" },
            new[] { "Paper" });

        var review = VisualAiCropReviewSummary.From(artifact, sourceEvidence, objectToCropAreaRatio: 0.145);

        Assert.Equal("symbol-labeling-candidate", review.SuggestedTrainingUse);
        Assert.Equal("High", review.Priority);
        Assert.Contains("representative crop for a grouped symbol family", review.Reasons);
    }

    [Fact]
    public async Task JsonExporter_WritesVisualAiEvidenceForObjectsAndGroups()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            CreateRepeatedSymbolDocument(),
            new ScannerOptions
            {
                EnableVisualAiClassification = true,
                VisualAiClassifier = new TestVisualAiClassifier(
                    new VisualAiClassificationResult(
                        "plan-symbol-test-model",
                        "2026.06",
                        "test-classifier",
                        new VisualAiClassificationCandidate(
                            "sofa",
                            ObjectCategory.Furniture,
                            0.88,
                            new Dictionary<string, string> { ["classIndex"] = "2" }),
                        new[]
                        {
                            new VisualAiClassificationCandidate(
                                "sofa",
                                ObjectCategory.Furniture,
                                0.88,
                                new Dictionary<string, string> { ["classIndex"] = "2" })
                        },
                        new[] { "test classifier evidence" }))
            });
        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());

        var group = Assert.Single(parsed.RootElement.GetProperty("objectGroups").EnumerateArray(), item =>
            item.GetProperty("symbolName").GetString() == "ISO_TAG_71");
        var groupVisualAi = group.GetProperty("visualAi");
        Assert.Equal("sofa", groupVisualAi.GetProperty("label").GetString());
        Assert.Equal("Furniture", groupVisualAi.GetProperty("category").GetString());
        Assert.Equal("plan-symbol-test-model", groupVisualAi.GetProperty("modelName").GetString());
        Assert.Equal("2026.06", groupVisualAi.GetProperty("modelVersion").GetString());
        Assert.Equal("test-classifier", groupVisualAi.GetProperty("inferenceEngine").GetString());
        Assert.True(groupVisualAi.GetProperty("confidence").GetDouble() > 0.8);
        Assert.Equal(1, groupVisualAi.GetProperty("pageNumber").GetInt32());
        Assert.Equal("sofa", groupVisualAi.GetProperty("alternatives")[0].GetProperty("label").GetString());

        var objectVisualAi = parsed.RootElement
            .GetProperty("objects")
            .EnumerateArray()
            .First(item => item.GetProperty("symbolName").GetString() == "ISO_TAG_71")
            .GetProperty("visualAi");
        Assert.Equal("sofa", objectVisualAi.GetProperty("label").GetString());
        Assert.Equal("Furniture", objectVisualAi.GetProperty("category").GetString());
        Assert.Equal("test classifier evidence", objectVisualAi.GetProperty("evidence")[0].GetString());
    }

    private static PlanDocument CreateRepeatedSymbolDocument() =>
        new(
            "visual-ai-symbols",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 500),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(80, 80), new PlanPoint(620, 80)),
                        Wall("wall-right", new PlanPoint(620, 80), new PlanPoint(620, 420)),
                        Wall("wall-bottom", new PlanPoint(620, 420), new PlanPoint(80, 420)),
                        Wall("wall-left", new PlanPoint(80, 420), new PlanPoint(80, 80)),
                        TagText("tag-1", "P-101", new PlanRect(180, 164, 46, 14)),
                        Symbol("unknown-1", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(180, 180, 26, 26)),
                        TagText("tag-2", "P-102", new PlanRect(320, 204, 46, 14)),
                        Symbol("unknown-2", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(320, 220, 26, 26)),
                        Symbol("unknown-3", "ISO_TAG_99", "X-SYMBOLS", new PlanRect(430, 260, 24, 24))
                    })
            });

    private static PlanDocument CreateTinySymbolDocument() =>
        new(
            "visual-ai-tiny-symbol",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(300, 220),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(40, 40), new PlanPoint(260, 40)),
                        Wall("wall-right", new PlanPoint(260, 40), new PlanPoint(260, 180)),
                        Wall("wall-bottom", new PlanPoint(260, 180), new PlanPoint(40, 180)),
                        Wall("wall-left", new PlanPoint(40, 180), new PlanPoint(40, 40)),
                        Symbol("tiny-symbol", "ISO_TAG_1", "X-SYMBOLS", new PlanRect(120, 100, 10, 10))
                    })
            });

    private static LinePrimitive Wall(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-WALL",
            Source = Source(sourceId, "LINE", "A-WALL")
        };

    private static TextPrimitive TagText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "I-TAG",
            Source = Source(sourceId, "TEXT", "I-TAG")
        };

    private static SymbolPrimitive Symbol(string sourceId, string name, string layer, PlanRect bounds) =>
        new(name, bounds)
        {
            SourceId = sourceId,
            Layer = layer,
            Source = Source(sourceId, "INSERT", layer, blockName: name)
        };

    private static PrimitiveSourceMetadata Source(
        string sourceId,
        string entityType,
        string layer,
        string? blockName = null) =>
        new()
        {
            SourceFormat = "test",
            SourceId = sourceId,
            EntityType = entityType,
            Layer = layer,
            BlockName = blockName,
            DrawingSpace = SourceDrawingSpace.Model
        };

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

    private sealed class TestVisualAiClassifier : IVisualAiObjectClassifier
    {
        private readonly VisualAiClassificationResult _result;

        public TestVisualAiClassifier(VisualAiClassificationResult result)
        {
            _result = result;
        }

        public List<VisualAiClassificationRequest> Requests { get; } = new();

        public ValueTask<VisualAiClassificationResult?> ClassifyAsync(
            VisualAiClassificationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Assert.Equal(3, request.Crop.Channels);
            Assert.True(request.Crop.Width > 0);
            Assert.True(request.Crop.Height > 0);
            Assert.NotEmpty(request.SourcePrimitiveIds);
            return ValueTask.FromResult<VisualAiClassificationResult?>(_result);
        }
    }
}
