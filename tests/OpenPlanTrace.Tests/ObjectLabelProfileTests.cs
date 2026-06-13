using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class ObjectLabelProfileTests
{
    [Fact]
    public void ParseJson_ReadsSchemaVersionedObjectLabelRules()
    {
        var profile = ObjectLabelProfile.ParseJson(
            """
            {
              "schemaVersion": "openplantrace.object-label-profile.v1",
              "name": "Plant symbols",
              "version": "1.0",
              "rules": [
                {
                  "signature": "symbol:iso_tag_71|category:GenericSymbol|kind:Symbol|layers:x-symbols",
                  "detectedTagPattern": "P-*",
                  "category": "Equipment",
                  "label": "Isolation valve",
                  "symbolName": "VALVE_ISO",
                  "requiresReview": false,
                  "confidence": 0.91,
                  "evidence": ["user confirmed ISO_TAG_71"]
                }
              ]
            }
            """);

        var rule = Assert.Single(profile.Rules);
        Assert.Equal(ObjectLabelProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Equal("Plant symbols", profile.Name);
        Assert.Equal("P-*", rule.DetectedTagPattern);
        Assert.Equal(ObjectCategory.Equipment, rule.Category);
        Assert.Equal("Isolation valve", rule.Label);
        Assert.Equal("VALVE_ISO", rule.SymbolName);
        Assert.False(rule.RequiresReview);
        Assert.Equal(0.91, rule.Confidence!.Value.Value, 3);
        Assert.Contains("user confirmed ISO_TAG_71", rule.Evidence);
    }

    [Fact]
    public void ParseJson_RejectsSelectorlessObjectLabelRules()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ObjectLabelProfile.ParseJson(
                """
                {
                  "schemaVersion": "openplantrace.object-label-profile.v1",
                  "rules": [
                    {
                      "category": "Equipment",
                      "label": "Pump"
                    }
                  ]
                }
                """));

        Assert.Contains("requires at least one selector", exception.Message);
    }

    [Fact]
    public async Task ScanAsync_AppliesObjectLabelProfileDetectedTagPatternToTaggedSymbols()
    {
        var document = new PlanDocument(
            "profile-tagged-symbols",
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
                        Symbol("pump-1", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(180, 180, 26, 26)),
                        TagText("pump-tag-1", "P-101", new PlanRect(212, 184, 48, 14)),
                        Symbol("pump-2", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(320, 220, 26, 26)),
                        TagText("pump-tag-2", "P-102", new PlanRect(352, 224, 48, 14))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                ObjectLabelRules = new[]
                {
                    new ObjectLabelRule
                    {
                        DetectedTagPattern = "P-*",
                        Category = ObjectCategory.Equipment,
                        Label = "Pump",
                        SymbolName = "PUMP_TAGGED",
                        RequiresReview = false,
                        Confidence = new Confidence(0.93),
                        Evidence = new[] { "user confirmed pump tag pattern" }
                    }
                }
            });

        var group = Assert.Single(result.ObjectGroups, group => group.SymbolName == "PUMP_TAGGED");
        Assert.Equal("Pump", group.Label);
        Assert.Equal(new[] { "P-101", "P-102" }, group.DetectedTags);
        Assert.False(group.RequiresReview);
        Assert.Contains(group.Evidence, item => item.Contains("detected tag pattern 'P-*'", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(group.Evidence, item => item.Contains("user confirmed pump tag pattern", StringComparison.OrdinalIgnoreCase));

        var symbols = result.ObjectCandidates
            .Where(candidate => candidate.SourcePrimitiveIds.Contains("pump-1") || candidate.SourcePrimitiveIds.Contains("pump-2"))
            .ToArray();
        Assert.Equal(2, symbols.Length);
        Assert.All(symbols, candidate =>
        {
            Assert.Equal("Pump", candidate.Label);
            Assert.Equal("PUMP_TAGGED", candidate.SymbolName);
            Assert.Contains(candidate.Evidence, item => item.Contains("object label profile applied", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task ScanAsync_AppliesObjectLabelProfileToRepeatedUnknownSymbolGroupAndCandidates()
    {
        var document = new PlanDocument(
            "profile-labeled-symbols",
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
                        Symbol("unknown-1", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(180, 180, 26, 26)),
                        Symbol("unknown-2", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(320, 220, 26, 26))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                ObjectLabelRules = new[]
                {
                    new ObjectLabelRule
                    {
                        Signature = "symbol:iso_tag_71|category:GenericSymbol|kind:Symbol|layers:x-symbols",
                        Category = ObjectCategory.Equipment,
                        Label = "Isolation valve",
                        SymbolName = "VALVE_ISO",
                        RequiresReview = false,
                        Confidence = new Confidence(0.91),
                        Evidence = new[] { "user confirmed repeated ISO_TAG_71 valve symbol" }
                    }
                }
            });

        var group = Assert.Single(result.ObjectGroups);
        Assert.Equal(ObjectCategory.Equipment, group.Category);
        Assert.Equal("Isolation valve", group.Label);
        Assert.Equal("VALVE_ISO", group.SymbolName);
        Assert.False(group.RequiresReview);
        Assert.InRange(group.Confidence.Value, 0.91, 0.96);
        Assert.Contains(group.Evidence, item => item.Contains("object label profile matched signature", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(group.Evidence, item => item.Contains("user confirmed repeated ISO_TAG_71", StringComparison.OrdinalIgnoreCase));

        Assert.All(result.ObjectCandidates, candidate =>
        {
            Assert.Equal(ObjectCategory.Equipment, candidate.Category);
            Assert.Equal("Isolation valve", candidate.Label);
            Assert.Equal("VALVE_ISO", candidate.SymbolName);
            Assert.Contains(candidate.Evidence, item => item.Contains("object label profile applied", StringComparison.OrdinalIgnoreCase));
        });

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "object_groups.detected"));
        Assert.Equal("1", diagnostic.Properties["profileRuleCount"]);
        Assert.Equal("1", diagnostic.Properties["profileLabeledGroupCount"]);
    }

    [Fact]
    public async Task JsonExporter_IncludesProfileLabeledObjectsAndGroups()
    {
        var document = new PlanDocument(
            "profile-labeled-export",
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
                        Symbol("unknown-1", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(180, 180, 26, 26)),
                        Symbol("unknown-2", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(320, 220, 26, 26))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                ObjectLabelRules = new[]
                {
                    new ObjectLabelRule
                    {
                        SymbolNamePattern = "ISO_TAG_*",
                        LayerPattern = "X-SYMBOLS",
                        Category = ObjectCategory.Equipment,
                        Label = "Tagged industrial device",
                        RequiresReview = false
                    }
                }
            });

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var group = Assert.Single(parsed.RootElement.GetProperty("objectGroups").EnumerateArray());
        var objects = parsed.RootElement.GetProperty("objects").EnumerateArray().ToArray();

        Assert.Equal("Equipment", group.GetProperty("category").GetString());
        Assert.Equal("Tagged industrial device", group.GetProperty("label").GetString());
        Assert.False(group.GetProperty("requiresReview").GetBoolean());
        Assert.Equal(2, objects.Length);
        Assert.All(objects, item =>
        {
            Assert.Equal("Equipment", item.GetProperty("category").GetString());
            Assert.Equal("Tagged industrial device", item.GetProperty("label").GetString());
        });
    }

    [Fact]
    public async Task TemplateBuilder_SuggestsDetectedTagPatternForCommonTagPrefix()
    {
        var document = new PlanDocument(
            "template-tagged-symbols",
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
                        Symbol("pump-1", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(180, 180, 26, 26)),
                        TagText("pump-tag-1", "P-101", new PlanRect(212, 184, 48, 14)),
                        Symbol("pump-2", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(320, 220, 26, 26)),
                        TagText("pump-tag-2", "P-102", new PlanRect(352, 224, 48, 14))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var profile = ObjectLabelProfileTemplateBuilder.FromScanResult(result);

        var rule = Assert.Single(profile.Rules);
        Assert.Equal("P-*", rule.DetectedTagPattern);
        Assert.Contains(rule.Evidence, item => item.Contains("Detected tags: P-101, P-102", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TemplateBuilder_SuggestsCompactDetectedTagPatternForCommonTagPrefix()
    {
        var document = new PlanDocument(
            "template-compact-tagged-symbols",
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
                        Symbol("pump-1", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(180, 180, 26, 26)),
                        TagText("pump-tag-1", "P14", new PlanRect(212, 184, 36, 14)),
                        Symbol("pump-2", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(320, 220, 26, 26)),
                        TagText("pump-tag-2", "P27", new PlanRect(352, 224, 36, 14))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var profile = ObjectLabelProfileTemplateBuilder.FromScanResult(result);

        var rule = Assert.Single(profile.Rules);
        Assert.Equal("P*", rule.DetectedTagPattern);
        Assert.Contains(rule.Evidence, item => item.Contains("Detected tags: P14, P27", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TemplateBuilder_CreatesEditableRulesFromObjectGroups()
    {
        var document = new PlanDocument(
            "template-symbols",
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
                        Symbol("unknown-1", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(180, 180, 26, 26)),
                        Symbol("unknown-2", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(320, 220, 26, 26))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var profile = ObjectLabelProfileTemplateBuilder.FromScanResult(result);

        var rule = Assert.Single(profile.Rules);
        Assert.Equal(ObjectLabelProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Equal("template-symbols object label draft", profile.Name);
        Assert.Equal("draft", profile.Version);
        Assert.Equal("symbol:iso_tag_71|category:GenericSymbol|kind:Symbol|layers:x-symbols", rule.Signature);
        Assert.Equal(ObjectCategory.GenericSymbol, rule.Category);
        Assert.Equal(ObjectCandidateKind.Symbol, rule.Kind);
        Assert.Equal("ISO_TAG_71", rule.SymbolName);
        Assert.True(rule.RequiresReview);
        Assert.InRange(rule.Confidence!.Value.Value, 0.34, 0.96);
        Assert.Contains(rule.Evidence, item => item.Contains("Drafted from OpenPlanTrace object group", StringComparison.Ordinal));
        Assert.Contains(rule.Evidence, item => item.Contains("Edit this rule", StringComparison.Ordinal));
    }

    [Fact]
    public void ObjectLabelProfileJsonSerializer_WritesParserCompatibleNumericConfidence()
    {
        var profile = new ObjectLabelProfile(
            ObjectLabelProfile.CurrentSchemaVersion,
            "Draft",
            "draft",
            new[]
            {
                new ObjectLabelRule
                {
                    Signature = "symbol:pump|category:GenericSymbol|kind:Symbol|layers:i-equip",
                    DetectedTagPattern = "P-*",
                    Category = ObjectCategory.Equipment,
                    Kind = ObjectCandidateKind.Symbol,
                    Label = "Pump",
                    RequiresReview = false,
                    Confidence = new Confidence(0.913),
                    Evidence = new[] { "User confirmed pump symbol." }
                }
            });

        var json = ObjectLabelProfileJsonSerializer.Serialize(profile, writeIndented: false);
        using var parsedJson = JsonDocument.Parse(json);
        var ruleJson = Assert.Single(parsedJson.RootElement.GetProperty("rules").EnumerateArray());

        Assert.Equal("P-*", ruleJson.GetProperty("detectedTagPattern").GetString());
        Assert.Equal(JsonValueKind.Number, ruleJson.GetProperty("confidence").ValueKind);
        Assert.Equal(0.91, ruleJson.GetProperty("confidence").GetDouble(), 2);

        var roundTripped = ObjectLabelProfile.ParseJson(json);
        var rule = Assert.Single(roundTripped.Rules);
        Assert.Equal("P-*", rule.DetectedTagPattern);
        Assert.Equal(ObjectCategory.Equipment, rule.Category);
        Assert.Equal("Pump", rule.Label);
        Assert.False(rule.RequiresReview);
        Assert.Equal(0.91, rule.Confidence!.Value.Value, 2);
    }

    private static LinePrimitive Wall(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-WALL",
            Source = Source(sourceId, "LINE", "A-WALL")
        };

    private static SymbolPrimitive Symbol(string sourceId, string name, string layer, PlanRect bounds) =>
        new(name, bounds)
        {
            SourceId = sourceId,
            Layer = layer,
            Source = Source(sourceId, "INSERT", layer, blockName: name)
        };

    private static TextPrimitive TagText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "P-TAG",
            Source = Source(sourceId, "TEXT", "P-TAG")
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
