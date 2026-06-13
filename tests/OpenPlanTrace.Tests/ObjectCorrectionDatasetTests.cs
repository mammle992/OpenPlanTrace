using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class ObjectCorrectionDatasetTests
{
    [Fact]
    public async Task Builder_CreatesUnreviewedCorrectionActionsFromObjectGroups()
    {
        var document = new PlanDocument(
            "object-corrections",
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
                        Symbol("unknown-1", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(220, 220, 28, 28)),
                        Text("tag-1", "P-101", "X-TAGS", new PlanRect(254, 224, 38, 12)),
                        Symbol("unknown-2", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(390, 245, 28, 28)),
                        Text("tag-2", "P-102", "X-TAGS", new PlanRect(424, 249, 38, 12))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var dataset = ObjectCorrectionDatasetBuilder.FromScanResult(result);

        Assert.Equal(ObjectCorrectionDataset.CurrentSchemaVersion, dataset.SchemaVersion);
        Assert.Equal(ObjectReviewDataset.CurrentSchemaVersion, dataset.SourceReviewDatasetSchemaVersion);

        var action = Assert.Single(dataset.Actions);
        Assert.Equal(ObjectCorrectionTargetKind.Group, action.TargetKind);
        Assert.Equal(ObjectCorrectionDecision.Unreviewed, action.Decision);
        Assert.Equal(ObjectCorrectionApplyScope.MatchingSignature, action.ApplyScope);
        Assert.Equal("symbol:iso_tag_71|category:Equipment|kind:Symbol|layers:x-symbols", action.Signature);
        Assert.Equal(ObjectCategory.Equipment, action.OriginalCategory);
        Assert.Equal(ObjectCandidateKind.Symbol, action.OriginalKind);
        Assert.Equal(ObjectCategory.Equipment, action.CorrectedCategory);
        Assert.Equal("ISO_TAG_71", action.CorrectedSymbolName);
        Assert.False(action.RequiresReview);
        Assert.NotNull(action.ReviewCropBounds);
        Assert.True(action.ReviewCropBounds.Value.Contains(new PlanRect(220, 220, 28, 28)));
        Assert.Equal(new[] { "P-101", "P-102" }, action.DetectedTags);
        Assert.Equal(new[] { 1 }, action.PageNumbers);
        Assert.Equal(2, action.CandidateIds.Count);
        Assert.Contains("X-SYMBOLS", action.SourceLayers);
        Assert.Contains(action.NearbyText, item => item.Text == "P-101" || item.Text == "P-102");
        Assert.Contains(action.Evidence, item => item.Contains("Change decision", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseJson_ConvertsConfirmedCorrectionsIntoObjectLabelProfileRules()
    {
        var dataset = ObjectCorrectionDataset.ParseJson(
            """
            {
              "schemaVersion": "openplantrace.object-correction-dataset.v1",
              "name": "Plant corrections",
              "version": "2026.06",
              "createdAt": "2026-06-06T00:00:00Z",
              "documentId": "plant-01",
              "actions": [
                {
                  "actionId": "group:valves",
                  "targetKind": "Group",
                  "decision": "Corrected",
                  "applyScope": "MatchingSignature",
                  "groupId": "object-group-1",
                  "signature": "symbol:iso_tag_71|category:GenericSymbol|kind:Symbol|layers:x-symbols",
                  "originalKind": "Symbol",
                  "originalCategory": "GenericSymbol",
                  "originalSymbolName": "ISO_TAG_71",
                  "correctedKind": "Symbol",
                  "correctedCategory": "Equipment",
                  "correctedLabel": "Isolation valve",
                  "correctedSymbolName": "VALVE_ISO",
                  "requiresReview": false,
                  "confidence": 0.91,
                  "pageNumbers": [1, 2],
                  "candidateIds": ["page:1:object:1", "page:1:object:2"],
                  "sourcePrimitiveIds": ["valve-a", "valve-b"],
                  "sourceLayers": ["X-SYMBOLS"],
                  "nearbyText": [],
                  "reviewer": "operator",
                  "reviewedAt": "2026-06-06T00:00:00Z",
                  "evidence": ["User confirmed repeated valve family."]
                },
                {
                  "actionId": "group:unreviewed",
                  "targetKind": "Group",
                  "decision": "Unreviewed",
                  "applyScope": "MatchingSignature",
                  "signature": "symbol:unknown|category:GenericSymbol|kind:Symbol|layers:x-symbols",
                  "correctedCategory": "GenericSymbol",
                  "candidateIds": [],
                  "sourcePrimitiveIds": [],
                  "sourceLayers": [],
                  "nearbyText": [],
                  "evidence": []
                }
              ]
            }
            """);

        var profile = dataset.ToObjectLabelProfile();
        var rule = Assert.Single(profile.Rules);

        Assert.Equal(ObjectLabelProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Equal("plant-01 object correction labels", profile.Name);
        Assert.Equal("2026.06", profile.Version);
        Assert.Equal("symbol:iso_tag_71|category:GenericSymbol|kind:Symbol|layers:x-symbols", rule.Signature);
        Assert.Equal(ObjectCategory.Equipment, rule.Category);
        Assert.Equal(ObjectCandidateKind.Symbol, rule.Kind);
        Assert.Equal("Isolation valve", rule.Label);
        Assert.Equal("VALVE_ISO", rule.SymbolName);
        Assert.False(rule.RequiresReview);
        Assert.Equal(0.91, rule.Confidence!.Value.Value, 3);
        Assert.Contains(rule.Evidence, item => item.Contains("2 reviewed occurrences", StringComparison.Ordinal));
        Assert.Contains(rule.Evidence, item => item.Contains("Reviewed occurrence pages: 1, 2", StringComparison.Ordinal));
        Assert.Contains(rule.Evidence, item => item.Contains("Human decision: Corrected", StringComparison.Ordinal));
        Assert.Contains(rule.Evidence, item => item.Contains("operator", StringComparison.Ordinal));
    }

    [Fact]
    public void ToObjectLabelProfile_RespectsConfirmedAndCorrectedFilters()
    {
        var dataset = ObjectCorrectionDataset.ParseJson(
            """
            {
              "schemaVersion": "openplantrace.object-correction-dataset.v1",
              "createdAt": "2026-06-06T00:00:00Z",
              "actions": [
                {
                  "actionId": "group:pumps",
                  "targetKind": "Group",
                  "decision": "Confirmed",
                  "applyScope": "MatchingSignature",
                  "signature": "symbol:pump|category:Equipment|kind:Symbol|layers:i-equip",
                  "originalKind": "Symbol",
                  "originalCategory": "Equipment",
                  "originalLabel": "Pump",
                  "requiresReview": false,
                  "candidateIds": [],
                  "sourcePrimitiveIds": [],
                  "sourceLayers": ["I-EQUIP"],
                  "nearbyText": [],
                  "evidence": []
                },
                {
                  "actionId": "group:valves",
                  "targetKind": "Group",
                  "decision": "Corrected",
                  "applyScope": "MatchingSignature",
                  "signature": "symbol:valve|category:GenericSymbol|kind:Symbol|layers:i-equip",
                  "originalKind": "Symbol",
                  "originalCategory": "GenericSymbol",
                  "correctedCategory": "Equipment",
                  "correctedLabel": "Isolation valve",
                  "requiresReview": false,
                  "candidateIds": [],
                  "sourcePrimitiveIds": [],
                  "sourceLayers": ["I-EQUIP"],
                  "nearbyText": [],
                  "evidence": []
                }
              ]
            }
            """);

        var confirmedOnly = dataset.ToObjectLabelProfile(
            new ObjectCorrectionLabelProfileOptions
            {
                IncludeConfirmed = true,
                IncludeCorrected = false
            });
        var correctedOnly = dataset.ToObjectLabelProfile(
            new ObjectCorrectionLabelProfileOptions
            {
                IncludeConfirmed = false,
                IncludeCorrected = true
            });

        Assert.Single(confirmedOnly.Rules);
        Assert.Equal("Pump", confirmedOnly.Rules[0].Label);
        Assert.Single(correctedOnly.Rules);
        Assert.Equal("Isolation valve", correctedOnly.Rules[0].Label);
    }

    [Fact]
    public void ToObjectLabelProfile_CreatesSymbolAndLayerRulesFromMatchingSymbolAndLayerScope()
    {
        var dataset = ObjectCorrectionDataset.ParseJson(
            """
            {
              "schemaVersion": "openplantrace.object-correction-dataset.v1",
              "createdAt": "2026-06-06T00:00:00Z",
              "actions": [
                {
                  "actionId": "group:panel",
                  "targetKind": "Group",
                  "decision": "Corrected",
                  "applyScope": "MatchingSymbolAndLayer",
                  "groupId": "object-group-7",
                  "originalKind": "Symbol",
                  "originalCategory": "GenericSymbol",
                  "originalSymbolName": "CAB_RECT",
                  "correctedKind": "Symbol",
                  "correctedCategory": "Equipment",
                  "correctedLabel": "Electrical panel",
                  "correctedSymbolName": "ELEC_PANEL",
                  "requiresReview": false,
                  "candidateIds": ["page:1:object:7"],
                  "sourcePrimitiveIds": ["panel-1"],
                  "sourceLayers": ["E-POWER"],
                  "nearbyText": [],
                  "reviewer": "operator",
                  "reviewedAt": "2026-06-06T00:00:00Z",
                  "evidence": ["Panel family reviewed."]
                }
              ]
            }
            """);

        var profile = dataset.ToObjectLabelProfile();
        var rule = Assert.Single(profile.Rules);

        Assert.Null(rule.Signature);
        Assert.Equal("ELEC_PANEL", rule.SymbolNamePattern);
        Assert.Equal("E-POWER", rule.LayerPattern);
        Assert.Equal(ObjectCategory.Equipment, rule.Category);
        Assert.Equal("Electrical panel", rule.Label);
        Assert.False(rule.RequiresReview);
        Assert.Contains(rule.Evidence, item => item.Contains("Panel family reviewed.", StringComparison.Ordinal));
    }

    [Fact]
    public void ToObjectLabelProfile_CreatesDetectedTagPatternRulesFromMatchingDetectedTagScope()
    {
        var dataset = ObjectCorrectionDataset.ParseJson(
            """
            {
              "schemaVersion": "openplantrace.object-correction-dataset.v1",
              "createdAt": "2026-06-06T00:00:00Z",
              "actions": [
                {
                  "actionId": "group:pumps",
                  "targetKind": "Group",
                  "decision": "Corrected",
                  "applyScope": "MatchingDetectedTagPattern",
                  "groupId": "object-group-pumps",
                  "originalKind": "Symbol",
                  "originalCategory": "GenericSymbol",
                  "originalSymbolName": "ISO_TAG_71",
                  "correctedKind": "Symbol",
                  "correctedCategory": "Equipment",
                  "correctedLabel": "Pump",
                  "correctedSymbolName": "PUMP_TAGGED",
                  "requiresReview": false,
                  "confidence": 0.92,
                  "detectedTags": ["P-101", "P-102"],
                  "candidateIds": ["page:1:object:1", "page:1:object:2"],
                  "sourcePrimitiveIds": ["pump-1", "pump-2"],
                  "sourceLayers": ["X-SYMBOLS"],
                  "nearbyText": [],
                  "reviewer": "operator",
                  "reviewedAt": "2026-06-06T00:00:00Z",
                  "evidence": ["Pump tag family reviewed."]
                },
                {
                  "actionId": "group:mixed-tags",
                  "targetKind": "Group",
                  "decision": "Corrected",
                  "applyScope": "MatchingDetectedTagPattern",
                  "originalKind": "Symbol",
                  "originalCategory": "GenericSymbol",
                  "correctedCategory": "Equipment",
                  "correctedLabel": "Mixed tag family",
                  "requiresReview": true,
                  "detectedTags": ["P-201", "TK-301"],
                  "candidateIds": [],
                  "sourcePrimitiveIds": [],
                  "sourceLayers": [],
                  "nearbyText": [],
                  "evidence": []
                }
              ]
            }
            """);

        var profile = dataset.ToObjectLabelProfile();
        var rule = Assert.Single(profile.Rules);

        Assert.Null(rule.Signature);
        Assert.Null(rule.SymbolNamePattern);
        Assert.Equal("P-*", rule.DetectedTagPattern);
        Assert.Equal(ObjectCategory.Equipment, rule.Category);
        Assert.Equal("Pump", rule.Label);
        Assert.Equal("PUMP_TAGGED", rule.SymbolName);
        Assert.False(rule.RequiresReview);
        Assert.Equal(0.92, rule.Confidence!.Value.Value, 3);
        Assert.Contains(rule.Evidence, item => item.Contains("Reviewed occurrence tags: P-101, P-102", StringComparison.Ordinal));
        Assert.Contains(rule.Evidence, item => item.Contains("detected tag pattern P-*", StringComparison.Ordinal));
        Assert.Contains(rule.Evidence, item => item.Contains("Pump tag family reviewed.", StringComparison.Ordinal));
    }

    [Fact]
    public void ToObjectLabelProfile_CreatesCompactDetectedTagPatternRulesFromMatchingDetectedTagScope()
    {
        var dataset = ObjectCorrectionDataset.ParseJson(
            """
            {
              "schemaVersion": "openplantrace.object-correction-dataset.v1",
              "createdAt": "2026-06-06T00:00:00Z",
              "actions": [
                {
                  "actionId": "group:compact-pumps",
                  "targetKind": "Group",
                  "decision": "Corrected",
                  "applyScope": "MatchingDetectedTagPattern",
                  "originalKind": "Symbol",
                  "originalCategory": "GenericSymbol",
                  "correctedCategory": "Equipment",
                  "correctedLabel": "Pump",
                  "requiresReview": false,
                  "detectedTags": ["P14", "P27"],
                  "candidateIds": ["page:1:object:1", "page:1:object:2"],
                  "sourcePrimitiveIds": [],
                  "sourceLayers": [],
                  "nearbyText": [],
                  "evidence": ["Compact pump tags reviewed."]
                }
              ]
            }
            """);

        var profile = dataset.ToObjectLabelProfile();
        var rule = Assert.Single(profile.Rules);

        Assert.Equal("P*", rule.DetectedTagPattern);
        Assert.Equal(ObjectCategory.Equipment, rule.Category);
        Assert.Equal("Pump", rule.Label);
        Assert.Contains(rule.Evidence, item => item.Contains("Reviewed occurrence tags: P14, P27", StringComparison.Ordinal));
        Assert.Contains(rule.Evidence, item => item.Contains("detected tag pattern P*", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseJson_RejectsReviewedCorrectionsWithoutOutputs()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ObjectCorrectionDataset.ParseJson(
                """
                {
                  "schemaVersion": "openplantrace.object-correction-dataset.v1",
                  "createdAt": "2026-06-06T00:00:00Z",
                  "actions": [
                    {
                      "actionId": "bad",
                      "targetKind": "Group",
                      "decision": "Confirmed",
                      "applyScope": "MatchingSignature",
                      "signature": "symbol:pump",
                      "candidateIds": [],
                      "sourcePrimitiveIds": [],
                      "sourceLayers": [],
                      "nearbyText": [],
                      "evidence": []
                    }
                  ]
                }
                """));

        Assert.Contains("no corrected label output", exception.Message);
    }

    [Fact]
    public void ParseJson_RejectsCorrectionActionsWithoutRequiredIdentityFields()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ObjectCorrectionDataset.ParseJson(
                """
                {
                  "schemaVersion": "openplantrace.object-correction-dataset.v1",
                  "createdAt": "2026-06-06T00:00:00Z",
                  "actions": [
                    {
                      "decision": "Unreviewed",
                      "applyScope": "TargetOnly",
                      "candidateIds": [],
                      "sourcePrimitiveIds": [],
                      "sourceLayers": [],
                      "nearbyText": [],
                      "evidence": []
                    }
                  ]
                }
                """));

        Assert.Contains("actionId is required", exception.Message);
    }

    [Fact]
    public void JsonSerializer_WritesSchemaVersionedMachineReadableJson()
    {
        var dataset = new ObjectCorrectionDataset(
            ObjectCorrectionDataset.CurrentSchemaVersion,
            "Corrections",
            "draft",
            DateTimeOffset.Parse("2026-06-06T00:00:00Z"),
            ObjectReviewDataset.CurrentSchemaVersion,
            "doc-1",
            "doc.dxf",
            null,
            new[]
            {
                new ObjectCorrectionAction(
                    "group:pump",
                    ObjectCorrectionTargetKind.Group,
                    ObjectCorrectionDecision.Corrected,
                    ObjectCorrectionApplyScope.MatchingSignature,
                    "group:pump",
                    null,
                    "symbol:pump|category:GenericSymbol|kind:Symbol|layers:i-equip",
                    ObjectCandidateKind.Symbol,
                    ObjectCategory.GenericSymbol,
                    null,
                    "PUMP_SYMBOL",
                    ObjectCandidateKind.Symbol,
                    ObjectCategory.Equipment,
                    "Process pump",
                    "PUMP",
                    false,
                    0.93,
                    new PlanRect(180, 180, 40, 40),
                    new[] { "P-101" },
                    new[] { 1 },
                    new[] { "page:1:object:1" },
                    new[] { "pump-1" },
                    new[] { "I-EQUIP" },
                    Array.Empty<ObjectReviewTextEvidence>(),
                    "operator",
                    DateTimeOffset.Parse("2026-06-06T00:00:00Z"),
                    new[] { "User confirmed pump." })
            });

        var json = ObjectCorrectionDatasetJsonSerializer.Serialize(dataset, writeIndented: false);
        using var parsed = JsonDocument.Parse(json);
        var action = Assert.Single(parsed.RootElement.GetProperty("actions").EnumerateArray());

        Assert.Equal(ObjectCorrectionDataset.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("Corrected", action.GetProperty("decision").GetString());
        Assert.Equal(JsonValueKind.Number, action.GetProperty("confidence").ValueKind);
        Assert.Equal(0.93, action.GetProperty("confidence").GetDouble(), 2);
        Assert.True(action.TryGetProperty("reviewCropBounds", out _));
        Assert.Contains("P-101", action.GetProperty("detectedTags").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(action.GetProperty("pageNumbers").EnumerateArray(), item => item.GetInt32() == 1);
    }

    private static LinePrimitive Wall(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-WALL",
            Source = Source(sourceId, "LINE", "A-WALL")
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
