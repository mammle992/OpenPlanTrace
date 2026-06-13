using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class ObjectReviewDatasetTests
{
    [Fact]
    public async Task ObjectReviewDatasetBuilder_ExportsReviewGroupsCandidatesNearbyTextAndSuggestedRules()
    {
        var document = new PlanDocument(
            "object-review",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(760, 520),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(80, 80), new PlanPoint(660, 80)),
                        Wall("wall-right", new PlanPoint(660, 80), new PlanPoint(660, 430)),
                        Wall("wall-bottom", new PlanPoint(660, 430), new PlanPoint(80, 430)),
                        Wall("wall-left", new PlanPoint(80, 430), new PlanPoint(80, 80)),
                        RoomText("room-label", "PUMP ROOM", new PlanRect(300, 125, 90, 16)),
                        Symbol("unknown-1", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(220, 220, 28, 28)),
                        Text("tag-1", "P-101", "X-TAGS", new PlanRect(254, 224, 38, 12)),
                        Symbol("unknown-2", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(390, 245, 28, 28)),
                        Text("tag-2", "P-102", "X-TAGS", new PlanRect(424, 249, 38, 12))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var dataset = ObjectReviewDatasetBuilder.FromScanResult(result);

        Assert.Equal(ObjectReviewDataset.CurrentSchemaVersion, dataset.SchemaVersion);
        Assert.Equal("object-review object review dataset", dataset.Name);

        var group = Assert.Single(dataset.Groups, item => item.SymbolName == "ISO_TAG_71");
        Assert.Equal("symbol:iso_tag_71|category:Equipment|kind:Symbol|layers:x-symbols", group.Signature);
        Assert.False(group.RequiresReview);
        Assert.Equal(2, group.Count);
        Assert.Equal(2, group.Candidates.Count);
        Assert.Equal(new[] { "P-101", "P-102" }, group.DetectedTags);
        Assert.True(group.ReviewCropBounds.Contains(group.RepresentativeBounds));
        Assert.True(group.ReviewCropBounds.Width > group.RepresentativeBounds.Width);
        Assert.True(group.ReviewCropBounds.Height > group.RepresentativeBounds.Height);
        Assert.Contains("X-SYMBOLS", group.SourceLayers);
        Assert.Equal(group.Signature, group.SuggestedRule.Signature);
        Assert.Equal(ObjectCategory.Equipment, group.SuggestedRule.Category);
        Assert.Equal(ObjectCandidateKind.Symbol, group.SuggestedRule.Kind);
        Assert.False(group.SuggestedRule.RequiresReview);
        Assert.InRange(group.Confidence, 0.35, 0.96);
        Assert.Contains(group.NearbyText, item => item.Text == "P-101" || item.Text == "P-102");

        var firstCandidate = Assert.Single(group.Candidates, item => item.SymbolName == "ISO_TAG_71" && item.CandidateId == "page:1:object:1");
        Assert.Equal(group.GroupId, firstCandidate.GroupId);
        Assert.Equal("P-101", firstCandidate.DetectedTag);
        Assert.Equal("tag-1", firstCandidate.DetectedTagSourcePrimitiveId);
        Assert.True(firstCandidate.ReviewCropBounds.Contains(firstCandidate.Bounds));
        Assert.Contains("PUMP ROOM", firstCandidate.RoomLabel);
        Assert.Contains("X-SYMBOLS", firstCandidate.SourceLayers);
        Assert.Contains(firstCandidate.NearbyText, item => item.Text == "P-101");
    }

    [Fact]
    public async Task ObjectReviewDatasetJsonSerializer_WritesSchemaVersionedMachineReadableJson()
    {
        var document = new PlanDocument(
            "object-review-json",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(560, 420),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(80, 80), new PlanPoint(480, 80)),
                        Wall("wall-right", new PlanPoint(480, 80), new PlanPoint(480, 340)),
                        Wall("wall-bottom", new PlanPoint(480, 340), new PlanPoint(80, 340)),
                        Wall("wall-left", new PlanPoint(80, 340), new PlanPoint(80, 80)),
                        Symbol("pump-1", "PROCESS_PUMP", "I-EQUIP", new PlanRect(210, 190, 26, 26)),
                        Symbol("pump-2", "PROCESS_PUMP", "I-EQUIP", new PlanRect(310, 190, 26, 26))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var dataset = ObjectReviewDatasetBuilder.FromScanResult(result);
        var json = ObjectReviewDatasetJsonSerializer.Serialize(dataset, writeIndented: false);
        using var parsed = JsonDocument.Parse(json);

        Assert.Equal(ObjectReviewDataset.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());

        var group = Assert.Single(parsed.RootElement.GetProperty("groups").EnumerateArray());
        Assert.Equal("Equipment", group.GetProperty("category").GetString());
        Assert.Equal("PROCESS_PUMP", group.GetProperty("symbolName").GetString());
        Assert.Equal(JsonValueKind.Number, group.GetProperty("confidence").ValueKind);
        Assert.Equal(JsonValueKind.Number, group.GetProperty("suggestedRule").GetProperty("confidence").ValueKind);
        Assert.True(group.TryGetProperty("reviewCropBounds", out _));
        Assert.Equal(2, group.GetProperty("candidates").GetArrayLength());
        Assert.True(group.GetProperty("candidates")[0].TryGetProperty("reviewCropBounds", out _));
        Assert.Equal("I-EQUIP", group.GetProperty("sourceLayers")[0].GetString());
    }

    [Fact]
    public async Task ObjectReviewDatasetBuilder_ClampsReviewCropBoundsToPage()
    {
        var document = new PlanDocument(
            "object-review-crop",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(120, 90),
                    new PlanPrimitive[]
                    {
                        Symbol("edge-symbol-1", "EDGE_TAG", "X-SYMBOLS", new PlanRect(2, 3, 16, 16)),
                        Symbol("edge-symbol-2", "EDGE_TAG", "X-SYMBOLS", new PlanRect(80, 62, 16, 16))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var dataset = ObjectReviewDatasetBuilder.FromScanResult(
            result,
            new ObjectReviewDatasetOptions { ReviewCropPadding = 25 });

        var group = Assert.Single(dataset.Groups);
        Assert.Equal(0, group.ReviewCropBounds.X);
        Assert.Equal(0, group.ReviewCropBounds.Y);
        Assert.True(group.ReviewCropBounds.Right <= 120);
        Assert.True(group.ReviewCropBounds.Bottom <= 90);
        Assert.All(group.Candidates, candidate =>
        {
            Assert.True(candidate.ReviewCropBounds.Contains(candidate.Bounds));
            Assert.True(candidate.ReviewCropBounds.X >= 0);
            Assert.True(candidate.ReviewCropBounds.Y >= 0);
            Assert.True(candidate.ReviewCropBounds.Right <= 120);
            Assert.True(candidate.ReviewCropBounds.Bottom <= 90);
        });
    }

    private static LinePrimitive Wall(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-WALL",
            Source = Source(sourceId, "LINE", "A-WALL")
        };

    private static TextPrimitive RoomText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "A-ROOM-NAME",
            Source = Source(sourceId, "TEXT", "A-ROOM-NAME")
        };

    private static TextPrimitive Text(string sourceId, string text, string layer, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = layer,
            Source = Source(sourceId, "TEXT", layer)
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
}
