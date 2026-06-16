using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class ObjectSemanticsTests
{
    [Fact]
    public async Task ScanAsync_ClassifiesCadSymbolsAndAssignsThemToRooms()
    {
        var document = new PlanDocument(
            "object-semantics",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(800, 600),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(100, 100), new PlanPoint(600, 100)),
                        Wall("wall-right", new PlanPoint(600, 100), new PlanPoint(600, 420)),
                        Wall("wall-bottom", new PlanPoint(600, 420), new PlanPoint(100, 420)),
                        Wall("wall-left", new PlanPoint(100, 420), new PlanPoint(100, 100)),
                        RoomText("room-label", "MECH 201", new PlanRect(310, 170, 90, 16)),
                        Symbol("ahu-1", "AHU_SUPPLY_FAN", "M-HVAC-EQPM", new PlanRect(300, 250, 42, 34)),
                        TagText("ahu-tag", "AHU-201", new PlanRect(300, 232, 62, 14)),
                        Symbol("sink-1", "SINK_DOUBLE", "P-PLUMB-FIXT", new PlanRect(430, 250, 28, 28))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.DoesNotContain(result.ObjectCandidates, candidate => candidate.Label == "MECH 201");

        var hvac = Assert.Single(result.ObjectCandidates, candidate => candidate.SymbolName == "AHU_SUPPLY_FAN");
        Assert.Equal(ObjectCategory.HVACEquipment, hvac.Category);
        Assert.Equal("MECH 201", hvac.RoomLabel);
        Assert.Contains(hvac.NearbyText, item => item.Text == "AHU-201");
        Assert.Contains(hvac.Evidence, item => item.Contains("hvac", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hvac.Evidence, item => item.Contains("nearby text", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hvac.Evidence, item => item.Contains("assigned to room", StringComparison.OrdinalIgnoreCase));

        var plumbing = Assert.Single(result.ObjectCandidates, candidate => candidate.SymbolName == "SINK_DOUBLE");
        Assert.Equal(ObjectCategory.PlumbingFixture, plumbing.Category);
        Assert.Equal(hvac.RoomId, plumbing.RoomId);
    }

    [Fact]
    public async Task ScanAsync_UsesNearbyTextToClassifyGenericCadSymbols()
    {
        var document = new PlanDocument(
            "nearby-text-object-semantics",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(800, 600),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(100, 100), new PlanPoint(600, 100)),
                        Wall("wall-right", new PlanPoint(600, 100), new PlanPoint(600, 420)),
                        Wall("wall-bottom", new PlanPoint(600, 420), new PlanPoint(100, 420)),
                        Wall("wall-left", new PlanPoint(100, 420), new PlanPoint(100, 100)),
                        RoomText("room-label", "PROCESS", new PlanRect(310, 170, 90, 16)),
                        Symbol("valve-1", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(250, 240, 26, 26)),
                        TagText("valve-tag-1", "VALVE HV-101", new PlanRect(282, 244, 78, 14)),
                        Symbol("valve-2", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(390, 260, 26, 26)),
                        TagText("valve-tag-2", "VALVE HV-102", new PlanRect(422, 264, 78, 14))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var valves = result.ObjectCandidates
            .Where(candidate => candidate.SymbolName == "ISO_TAG_71")
            .OrderBy(candidate => candidate.Bounds.Left)
            .ToArray();

        Assert.Equal(2, valves.Length);
        Assert.All(valves, candidate =>
        {
            Assert.Equal(ObjectCategory.Equipment, candidate.Category);
            Assert.Equal(ObjectCandidateKind.Symbol, candidate.Kind);
            Assert.Equal("PROCESS", candidate.RoomLabel);
            Assert.Contains(candidate.NearbyText, item => item.Text.StartsWith("VALVE", StringComparison.Ordinal));
            Assert.Contains(candidate.Evidence, item => item.Contains("nearby text", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(candidate.Evidence, item => item.Contains("matches 'valve'", StringComparison.OrdinalIgnoreCase));
        });

        var group = Assert.Single(result.ObjectGroups, group => group.SymbolName == "ISO_TAG_71");
        Assert.Equal(ObjectCategory.Equipment, group.Category);
        Assert.False(group.RequiresReview);
        Assert.Equal(2, group.Count);
        Assert.Contains("VALVE HV-101", group.NearbyText.Select(item => item.Text));
        Assert.Contains("VALVE HV-102", group.NearbyText.Select(item => item.Text));
    }

    [Fact]
    public async Task ScanAsync_UsesIndustrialTagCodesToClassifyGenericSymbols()
    {
        var document = new PlanDocument(
            "industrial-tag-object-semantics",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(800, 600),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(100, 100), new PlanPoint(600, 100)),
                        Wall("wall-right", new PlanPoint(600, 100), new PlanPoint(600, 420)),
                        Wall("wall-bottom", new PlanPoint(600, 420), new PlanPoint(100, 420)),
                        Wall("wall-left", new PlanPoint(100, 420), new PlanPoint(100, 100)),
                        RoomText("room-label", "PROCESS", new PlanRect(310, 170, 90, 16)),
                        Symbol("pump-symbol-1", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(250, 240, 26, 26)),
                        TagText("pump-tag-1", "P-101", new PlanRect(282, 244, 48, 14)),
                        Symbol("pump-symbol-2", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(390, 260, 26, 26)),
                        TagText("pump-tag-2", "TK-201", new PlanRect(422, 264, 58, 14))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var taggedSymbols = result.ObjectCandidates
            .Where(candidate => candidate.SymbolName == "ISO_TAG_71")
            .OrderBy(candidate => candidate.Bounds.Left)
            .ToArray();

        Assert.Equal(2, taggedSymbols.Length);
        Assert.All(taggedSymbols, candidate =>
        {
            Assert.Equal(ObjectCategory.Equipment, candidate.Category);
            Assert.Equal(ObjectCandidateKind.Symbol, candidate.Kind);
            Assert.Equal("PROCESS", candidate.RoomLabel);
            Assert.Contains(candidate.Evidence, item => item.Contains("industrial tag", StringComparison.OrdinalIgnoreCase));
        });
        Assert.Contains(taggedSymbols[0].NearbyText, item => item.Text == "P-101");
        Assert.Contains(taggedSymbols[1].NearbyText, item => item.Text == "TK-201");
        Assert.Equal("P-101", taggedSymbols[0].DetectedTag);
        Assert.Equal("pump-tag-1", taggedSymbols[0].DetectedTagSourcePrimitiveId);
        Assert.Equal("TK-201", taggedSymbols[1].DetectedTag);
        Assert.Equal("pump-tag-2", taggedSymbols[1].DetectedTagSourcePrimitiveId);

        var group = Assert.Single(result.ObjectGroups, group => group.SymbolName == "ISO_TAG_71");
        Assert.Equal(ObjectCategory.Equipment, group.Category);
        Assert.False(group.RequiresReview);
        Assert.Equal(2, group.Count);
        Assert.Equal(new[] { "P-101", "TK-201" }, group.DetectedTags);
        Assert.Contains("P-101", group.NearbyText.Select(item => item.Text));
        Assert.Contains("TK-201", group.NearbyText.Select(item => item.Text));
    }

    [Fact]
    public async Task ScanAsync_UsesCompactIndustrialTagCodesToClassifyGenericSymbols()
    {
        var document = new PlanDocument(
            "compact-industrial-tag-object-semantics",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(800, 600),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(100, 100), new PlanPoint(600, 100)),
                        Wall("wall-right", new PlanPoint(600, 100), new PlanPoint(600, 420)),
                        Wall("wall-bottom", new PlanPoint(600, 420), new PlanPoint(100, 420)),
                        Wall("wall-left", new PlanPoint(100, 420), new PlanPoint(100, 100)),
                        Symbol("pump-symbol-1", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(250, 240, 26, 26)),
                        TagText("pump-tag-1", "P14", new PlanRect(282, 244, 36, 14)),
                        Symbol("pump-symbol-2", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(390, 260, 26, 26)),
                        TagText("pump-tag-2", "P27", new PlanRect(422, 264, 36, 14))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var taggedSymbols = result.ObjectCandidates
            .Where(candidate => candidate.SymbolName == "ISO_TAG_71")
            .OrderBy(candidate => candidate.Bounds.Left)
            .ToArray();

        Assert.Equal(2, taggedSymbols.Length);
        Assert.All(taggedSymbols, candidate =>
        {
            Assert.Equal(ObjectCategory.Equipment, candidate.Category);
            Assert.Equal(ObjectCandidateKind.Symbol, candidate.Kind);
            Assert.Contains(candidate.Evidence, item => item.Contains("industrial tag", StringComparison.OrdinalIgnoreCase));
        });
        Assert.Equal("P14", taggedSymbols[0].DetectedTag);
        Assert.Equal("pump-tag-1", taggedSymbols[0].DetectedTagSourcePrimitiveId);
        Assert.Equal("P27", taggedSymbols[1].DetectedTag);
        Assert.Equal("pump-tag-2", taggedSymbols[1].DetectedTagSourcePrimitiveId);

        var group = Assert.Single(result.ObjectGroups, group => group.SymbolName == "ISO_TAG_71");
        Assert.Equal(ObjectCategory.Equipment, group.Category);
        Assert.False(group.RequiresReview);
        Assert.Equal(new[] { "P14", "P27" }, group.DetectedTags);
    }

    [Fact]
    public async Task ScanAsync_DoesNotClassifyRoomOrSheetCodesAsIndustrialEquipmentTags()
    {
        var document = new PlanDocument(
            "industrial-tag-false-positive-guard",
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
                        Symbol("unknown-1", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(220, 210, 26, 26)),
                        TagText("room-like-code", "MECH-201", new PlanRect(252, 214, 68, 14)),
                        Symbol("unknown-2", "ISO_TAG_99", "X-SYMBOLS", new PlanRect(390, 250, 26, 26)),
                        TagText("sheet-like-code", "A-101", new PlanRect(422, 254, 48, 14)),
                        Symbol("unknown-3", "ISO_TAG_100", "X-SYMBOLS", new PlanRect(250, 300, 26, 26)),
                        TagText("short-plan-code", "P1", new PlanRect(282, 304, 24, 14))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var symbols = result.ObjectCandidates
            .Where(candidate => candidate.SymbolName is "ISO_TAG_71" or "ISO_TAG_99" or "ISO_TAG_100")
            .ToArray();

        Assert.Equal(3, symbols.Length);
        Assert.All(symbols, candidate =>
        {
            Assert.Equal(ObjectCategory.GenericSymbol, candidate.Category);
            Assert.DoesNotContain(candidate.Evidence, item => item.Contains("industrial tag", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task ScanAsync_DoesNotClassifyFurnitureAsHvacFromSubstringAir()
    {
        var document = new PlanDocument(
            "tokenized-object-semantics",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(80, 80), new PlanPoint(420, 80)),
                        Wall("wall-right", new PlanPoint(420, 80), new PlanPoint(420, 320)),
                        Wall("wall-bottom", new PlanPoint(420, 320), new PlanPoint(80, 320)),
                        Wall("wall-left", new PlanPoint(80, 320), new PlanPoint(80, 80)),
                        Symbol("chair-1", "CHAIR_TASK", "A-FURN", new PlanRect(220, 180, 24, 24))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var chair = Assert.Single(result.ObjectCandidates, candidate => candidate.SymbolName == "CHAIR_TASK");

        Assert.Equal(ObjectCategory.Furniture, chair.Category);
        Assert.NotEqual(ObjectCategory.HVACEquipment, chair.Category);
        Assert.Contains(chair.Evidence, item => item.Contains("chair", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_ClassifiesClosedGeometryFromLayerHints()
    {
        var document = new PlanDocument(
            "closed-geometry-object",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(80, 80), new PlanPoint(420, 80)),
                        Wall("wall-right", new PlanPoint(420, 80), new PlanPoint(420, 320)),
                        Wall("wall-bottom", new PlanPoint(420, 320), new PlanPoint(80, 320)),
                        Wall("wall-left", new PlanPoint(80, 320), new PlanPoint(80, 80)),
                        new RectanglePrimitive(new PlanRect(180, 150, 22, 22))
                        {
                            SourceId = "column-1",
                            Layer = "S-COLUMN",
                            Source = Source("column-1", "RECTANGLE", "S-COLUMN")
                        }
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var column = Assert.Single(result.ObjectCandidates);

        Assert.Equal(ObjectCategory.Column, column.Category);
        Assert.NotNull(column.RoomId);
        Assert.Contains(column.Evidence, item => item.Contains("column", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_PromotesObjectLikeWallComponentsIntoReviewableObjectCandidates()
    {
        var document = new PlanDocument(
            "object-like-wall-component-candidate",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 500),
                    new PlanPrimitive[]
                    {
                        Wall("room-top", new PlanPoint(100, 100), new PlanPoint(600, 100)),
                        Wall("room-right", new PlanPoint(600, 100), new PlanPoint(600, 420)),
                        Wall("room-bottom", new PlanPoint(600, 420), new PlanPoint(100, 420)),
                        Wall("room-left", new PlanPoint(100, 420), new PlanPoint(100, 100)),
                        RoomText("room-label", "GARASJE", new PlanRect(330, 130, 72, 16)),
                        Wall("car-outline-top", new PlanPoint(300, 250), new PlanPoint(360, 250)),
                        Wall("car-outline-right", new PlanPoint(360, 250), new PlanPoint(360, 285)),
                        Wall("car-outline-bottom", new PlanPoint(360, 285), new PlanPoint(300, 285)),
                        Wall("car-outline-left", new PlanPoint(300, 285), new PlanPoint(300, 250))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var objectComponent = Assert.Single(
            result.WallGraph.Components,
            component => component.Kind == WallGraphComponentKind.ObjectLikeIsland);
        Assert.True(objectComponent.ExcludedFromStructuralTopology);

        var promoted = Assert.Single(
            result.ObjectCandidates,
            candidate => candidate.Evidence.Any(item => item.Contains(objectComponent.Id, StringComparison.Ordinal)));

        Assert.Equal(ObjectCategory.GenericSymbol, promoted.Category);
        Assert.Equal(ObjectCandidateKind.Symbol, promoted.Kind);
        Assert.Equal(ObjectCandidateSourceKind.WallComponentIsland, promoted.SourceKind);
        Assert.Equal(objectComponent.Id, promoted.SourceWallComponentId);
        Assert.Equal(WallGraphComponentKind.ObjectLikeIsland, promoted.SourceWallComponentKind);
        Assert.Equal(objectComponent.Bounds, promoted.Bounds);
        Assert.Equal("GARASJE", promoted.RoomLabel);
        Assert.Contains("car-outline-top", promoted.SourcePrimitiveIds);
        Assert.Contains("car-outline-right", promoted.SourcePrimitiveIds);
        Assert.Contains(promoted.Evidence, item => item.Contains("component excluded from structural topology", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(promoted.Evidence, item => item.Contains("possible object or symbol linework", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "objects.wall_component_islands.promoted"
                && diagnostic.Properties["candidateCount"] == "1"
                && diagnostic.Properties["componentIds"].Contains(objectComponent.Id, StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_evidence.object_like_components_reclassified"
                && diagnostic.Properties["componentIds"].Contains(objectComponent.Id, StringComparison.Ordinal));

        var objectWallAssessments = result.WallEvidenceMap.WallAssessments
            .Where(assessment => objectComponent.WallIds.Contains(assessment.WallId))
            .ToArray();
        Assert.Equal(objectComponent.WallCount, objectWallAssessments.Length);
        Assert.All(objectWallAssessments, assessment =>
        {
            Assert.Equal(WallEvidenceCategory.ObjectOrFixtureDetail, assessment.Category);
            Assert.Equal(WallEvidenceDecision.Reject, assessment.Decision);
            Assert.True(assessment.RejectedAsNoise);
            Assert.False(assessment.PlacementReady);
            Assert.True(assessment.ScoreBreakdown.NoisePenalty >= 0.75);
            Assert.Contains(assessment.Evidence, item => item.Contains(objectComponent.Id, StringComparison.Ordinal));
        });

        var group = Assert.Single(
            result.ObjectGroups,
            group => group.CandidateIds.Contains(promoted.Id));
        Assert.True(group.RequiresReview);
        Assert.Equal(ObjectCategory.GenericSymbol, group.Category);

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var promotedJson = Assert.Single(
            parsed.RootElement.GetProperty("objects").EnumerateArray(),
            item => item.GetProperty("id").GetString() == promoted.Id);
        Assert.Equal("WallComponentIsland", promotedJson.GetProperty("sourceKind").GetString());
        Assert.Equal(objectComponent.Id, promotedJson.GetProperty("sourceWallComponentId").GetString());
        Assert.Equal("ObjectLikeIsland", promotedJson.GetProperty("sourceWallComponentKind").GetString());
        var rejectedWallLikeDetails = parsed.RootElement.GetProperty("wallEvidence").GetProperty("rejectedWallLikeDetails").EnumerateArray().ToArray();
        Assert.Equal(objectComponent.WallCount, rejectedWallLikeDetails.Count(detail =>
            detail.GetProperty("category").GetString() == "ObjectOrFixtureDetail"));
        Assert.Contains(
            rejectedWallLikeDetails,
            detail => detail.GetProperty("sourcePrimitiveIds").EnumerateArray().Any(id => id.GetString() == "car-outline-top"));

        var geoJson = PlanTraceGeoJsonExporter.Serialize(result);
        using var parsedGeoJson = JsonDocument.Parse(geoJson);
        Assert.Contains(
            parsedGeoJson.RootElement.GetProperty("features").EnumerateArray(),
            feature => feature.GetProperty("properties").GetProperty("featureType").GetString() == "object"
                && feature.GetProperty("properties").GetProperty("openPlanTraceId").GetString() == promoted.Id
                && feature.GetProperty("properties").GetProperty("sourceKind").GetString() == "WallComponentIsland"
                && feature.GetProperty("properties").GetProperty("sourceWallComponentId").GetString() == objectComponent.Id);
    }

    [Fact]
    public async Task ScanAsync_CreatesCompositeLineworkObjectCandidatesFromLooseCadGeometry()
    {
        var document = new PlanDocument(
            "composite-linework-objects",
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
                        RoomText("room-label", "MECH 201", new PlanRect(310, 130, 90, 16)),
                        EquipmentLine("valve-a-1", new PlanPoint(200, 210), new PlanPoint(224, 210)),
                        EquipmentLine("valve-a-2", new PlanPoint(212, 198), new PlanPoint(212, 222)),
                        EquipmentLine("valve-a-3", new PlanPoint(204, 202), new PlanPoint(220, 218)),
                        EquipmentLine("valve-b-1", new PlanPoint(330, 260), new PlanPoint(354, 260)),
                        EquipmentLine("valve-b-2", new PlanPoint(342, 248), new PlanPoint(342, 272)),
                        EquipmentLine("valve-b-3", new PlanPoint(334, 252), new PlanPoint(350, 268))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var composites = result.ObjectCandidates
            .Where(candidate => candidate.Evidence.Any(item => item.Contains("composite linework", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(candidate => candidate.Bounds.Left)
            .ToArray();

        Assert.Equal(2, composites.Length);
        Assert.All(composites, candidate =>
        {
            Assert.Equal(ObjectCategory.Equipment, candidate.Category);
            Assert.Equal(ObjectCandidateKind.Symbol, candidate.Kind);
            Assert.Equal("MECH 201", candidate.RoomLabel);
            Assert.Equal(3, candidate.SourcePrimitiveIds.Count);
            Assert.Contains(candidate.Evidence, item => item.Contains("source layers: I-EQUIP-VALVE", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(candidate.Evidence, item => item.Contains("valve", StringComparison.OrdinalIgnoreCase));
        });

        var group = Assert.Single(result.ObjectGroups, group => group.Category == ObjectCategory.Equipment);
        Assert.Equal(2, group.Count);
        Assert.False(group.RequiresReview);
        Assert.Contains("geometry:25x25", group.Signature);
        Assert.Contains("valve-a-1", group.SourcePrimitiveIds);
        Assert.Contains("valve-b-3", group.SourcePrimitiveIds);
        Assert.Contains(group.Evidence, item => item.Contains("grouped 2 object candidate", StringComparison.OrdinalIgnoreCase));

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "objects.composite_linework.detected"));
        Assert.Equal("2", diagnostic.Properties["candidateCount"]);
        Assert.Equal("6", diagnostic.Properties["sourcePrimitiveCount"]);
        Assert.Contains("Equipment:2", diagnostic.Properties["categories"]);
    }

    [Fact]
    public async Task ScanAsync_CanDisableCompositeLineworkObjectCandidates()
    {
        var document = new PlanDocument(
            "composite-linework-disabled",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(80, 80), new PlanPoint(420, 80)),
                        Wall("wall-right", new PlanPoint(420, 80), new PlanPoint(420, 320)),
                        Wall("wall-bottom", new PlanPoint(420, 320), new PlanPoint(80, 320)),
                        Wall("wall-left", new PlanPoint(80, 320), new PlanPoint(80, 80)),
                        EquipmentLine("valve-a-1", new PlanPoint(200, 210), new PlanPoint(224, 210)),
                        EquipmentLine("valve-a-2", new PlanPoint(212, 198), new PlanPoint(212, 222))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions { DetectCompositeObjectCandidates = false });

        Assert.Empty(result.ObjectCandidates);
        Assert.DoesNotContain(result.Diagnostics.Messages, message => message.Code == "objects.composite_linework.detected");
    }

    [Fact]
    public async Task JsonExporter_IncludesObjectCategoryEvidenceAndRoom()
    {
        var document = new PlanDocument(
            "object-export",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(80, 80), new PlanPoint(420, 80)),
                        Wall("wall-right", new PlanPoint(420, 80), new PlanPoint(420, 320)),
                        Wall("wall-bottom", new PlanPoint(420, 320), new PlanPoint(80, 320)),
                        Wall("wall-left", new PlanPoint(80, 320), new PlanPoint(80, 80)),
                        RoomText("room-label", "LAB", new PlanRect(235, 130, 34, 16)),
                        TagText("pump-tag", "P-101", new PlanRect(250, 174, 46, 14)),
                        Symbol("pump-1", "PROCESS_PUMP", "I-EQUIP", new PlanRect(250, 190, 32, 32))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var objectJson = parsed.RootElement
            .GetProperty("objects")
            .EnumerateArray()
            .First(item => item.GetProperty("symbolName").GetString() == "PROCESS_PUMP");

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("Equipment", objectJson.GetProperty("category").GetString());
        Assert.Equal("PROCESS_PUMP", objectJson.GetProperty("symbolName").GetString());
        Assert.Equal("P-101", objectJson.GetProperty("detectedTag").GetString());
        Assert.Equal("pump-tag", objectJson.GetProperty("detectedTagSourcePrimitiveId").GetString());
        Assert.Equal("LAB", objectJson.GetProperty("roomLabel").GetString());
        Assert.Contains(
            objectJson.GetProperty("nearbyText").EnumerateArray(),
            item => item.GetProperty("text").GetString() == "P-101"
                && item.GetProperty("sourcePrimitiveId").GetString() == "pump-tag");
        Assert.True(objectJson.GetProperty("evidence").GetArrayLength() > 0);

        var pump = Assert.Single(result.ObjectCandidates, candidate => candidate.SymbolName == "PROCESS_PUMP");
        var routingObstacle = Assert.Single(
            result.RoutingLayer.Obstacles,
            obstacle => obstacle.SourceId == pump.Id);
        Assert.Equal(RoutingObstacleKind.HardObstacle, routingObstacle.ObstacleKind);
        Assert.Equal(ObjectRoutingInfluence.HardObstacle, routingObstacle.RoutingInfluence);
        Assert.Equal(ObjectStructuralInfluence.FixedEquipment, routingObstacle.StructuralInfluence);
        Assert.Equal(ObjectCategory.Equipment, routingObstacle.Category);

        var routingLayer = parsed.RootElement.GetProperty("routingLayer");
        var obstacleJson = Assert.Single(
            routingLayer.GetProperty("obstacles").EnumerateArray(),
            item => item.GetProperty("sourceId").GetString() == pump.Id);
        Assert.Equal("HardObstacle", obstacleJson.GetProperty("obstacleKind").GetString());
        Assert.Equal("FixedEquipment", obstacleJson.GetProperty("structuralInfluence").GetString());
        Assert.Contains("I-EQUIP", obstacleJson.GetProperty("sourceLayers").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task ScanAsync_GroupsRepeatedUnknownCadSymbolsForReview()
    {
        var document = new PlanDocument(
            "unknown-symbol-groups",
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
                        Symbol("unknown-2", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(320, 220, 26, 26)),
                        Symbol("unknown-3", "ISO_TAG_99", "X-SYMBOLS", new PlanRect(430, 260, 24, 24))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(3, result.ObjectCandidates.Count);
        Assert.Contains(result.ObjectCandidates, candidate => candidate.SymbolName == "ISO_TAG_71" && candidate.Category == ObjectCategory.GenericSymbol);

        var repeated = Assert.Single(result.ObjectGroups, group => group.SymbolName == "ISO_TAG_71");
        Assert.Equal(2, repeated.Count);
        Assert.True(repeated.RequiresReview);
        Assert.Equal(ObjectCategory.GenericSymbol, repeated.Category);
        Assert.Contains("unknown-1", repeated.SourcePrimitiveIds);
        Assert.Contains("unknown-2", repeated.SourcePrimitiveIds);
        Assert.Contains(repeated.Evidence, item => item.Contains("review recommended", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(result.ObjectGroups, group => group.SymbolName == "ISO_TAG_99" && group.RequiresReview);
        Assert.Contains(result.Diagnostics.Messages, message => message.Code == "object_groups.detected");
    }

    [Fact]
    public async Task ScanAsync_DoesNotRouteUnreviewedGenericSymbolsAsObstacles()
    {
        var document = new PlanDocument(
            "unreviewed-generic-symbol-routing",
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
                        RoomText("room-label", "OPEN AREA", new PlanRect(295, 130, 92, 16)),
                        Symbol("unknown-1", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(180, 180, 26, 26)),
                        Symbol("unknown-2", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(320, 220, 26, 26)),
                        Symbol("unknown-3", "DETAIL_MARK_9", "X-SYMBOLS", new PlanRect(430, 260, 24, 24))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var genericCandidates = result.ObjectCandidates
            .Where(candidate => candidate.SourcePrimitiveIds.Any(id => id.StartsWith("unknown-", StringComparison.Ordinal)))
            .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(3, genericCandidates.Length);
        Assert.NotEmpty(result.RoutingLayer.Barriers);
        Assert.Contains(result.ObjectGroups, group => group.SymbolName == "ISO_TAG_71" && group.RequiresReview);
        Assert.Empty(result.RoutingLayer.Obstacles);
        Assert.True(result.RoutingLayer.IgnoredObjects.Count >= genericCandidates.Length);

        foreach (var candidate in genericCandidates)
        {
            Assert.Contains(candidate.Id, result.RoutingLayer.IgnoredObjectCandidateIds);
            var ignored = Assert.Single(result.RoutingLayer.IgnoredObjects, item => item.ObjectCandidateId == candidate.Id);
            Assert.Equal(RoutingIgnoredObjectReason.UnclassifiedReviewCandidate, ignored.Reason);
            Assert.Equal(ObjectRoutingInfluence.Unknown, ignored.RoutingInfluence);
            Assert.Equal(candidate.Category, ignored.CandidateCategory);
            Assert.Equal(candidate.Kind, ignored.CandidateKind);
            Assert.Equal(candidate.Bounds, ignored.CandidateBounds);
            Assert.Contains(ignored.Evidence, item => item.Contains("needs review", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result.RoutingLayer.Obstacles, obstacle => obstacle.SourceId == candidate.Id);
            Assert.DoesNotContain(result.RoutingLayer.RoomUseHints, hint => hint.SourceId == candidate.Id);
        }

        var scanJson = PlanTraceJsonExporter.Serialize(result);
        using var parsedScan = JsonDocument.Parse(scanJson);
        var ignoredJson = parsedScan.RootElement
            .GetProperty("routingLayer")
            .GetProperty("ignoredObjects")
            .EnumerateArray()
            .ToArray();
        Assert.True(ignoredJson.Length >= genericCandidates.Length);
        Assert.Contains(
            ignoredJson,
            item => item.GetProperty("objectCandidateId").GetString() == genericCandidates[0].Id
                && item.GetProperty("reason").GetString() == "UnclassifiedReviewCandidate"
                && item.GetProperty("candidateBounds").ValueKind == JsonValueKind.Object);

        var placementJson = PlanPlacementJsonExporter.Serialize(result);
        using var parsedPlacement = JsonDocument.Parse(placementJson);
        Assert.Contains(
            parsedPlacement.RootElement.GetProperty("routingLayer").GetProperty("ignoredObjects").EnumerateArray(),
            item => item.GetProperty("objectCandidateId").GetString() == genericCandidates[0].Id
                && item.GetProperty("candidateCenter").ValueKind == JsonValueKind.Object
                && item.GetProperty("reason").GetString() == "UnclassifiedReviewCandidate");

        var geoJson = PlanTraceGeoJsonExporter.Serialize(result);
        using var parsedGeoJson = JsonDocument.Parse(geoJson);
        Assert.Contains(
            parsedGeoJson.RootElement.GetProperty("features").EnumerateArray(),
            feature => feature.GetProperty("properties").GetProperty("featureType").GetString() == "routingIgnoredObject"
                && feature.GetProperty("properties").GetProperty("objectCandidateId").GetString() == genericCandidates[0].Id
                && feature.GetProperty("geometry").GetProperty("type").GetString() == "Polygon");
    }

    [Fact]
    public async Task JsonExporter_IncludesObjectGroupsForRepeatedKnownSymbols()
    {
        var document = new PlanDocument(
            "object-group-export",
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
                        TagText("pump-tag-1", "P-101", new PlanRect(180, 164, 46, 14)),
                        Symbol("pump-1", "PROCESS_PUMP", "I-EQUIP", new PlanRect(180, 180, 26, 26)),
                        TagText("pump-tag-2", "P-102", new PlanRect(320, 204, 46, 14)),
                        Symbol("pump-2", "PROCESS_PUMP", "I-EQUIP", new PlanRect(320, 220, 26, 26))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());

        var group = Assert.Single(parsed.RootElement.GetProperty("objectGroups").EnumerateArray());
        Assert.Equal("Equipment", group.GetProperty("category").GetString());
        Assert.Equal("PROCESS_PUMP", group.GetProperty("symbolName").GetString());
        Assert.Equal(2, group.GetProperty("count").GetInt32());
        Assert.False(group.GetProperty("requiresReview").GetBoolean());
        Assert.Contains("P-101", group.GetProperty("detectedTags").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("P-102", group.GetProperty("detectedTags").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(2, group.GetProperty("candidateIds").GetArrayLength());
        Assert.Contains("I-EQUIP", group.GetProperty("sourceLayers").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            group.GetProperty("nearbyText").EnumerateArray(),
            item => item.GetProperty("text").GetString() == "P-101");
        Assert.Contains(
            group.GetProperty("nearbyText").EnumerateArray(),
            item => item.GetProperty("text").GetString() == "P-102");
        Assert.True(group.GetProperty("evidence").GetArrayLength() > 0);
    }

    [Fact]
    public async Task ScanAsync_AggregatesCompoundVehicleObjectAndPreservesChildDetections()
    {
        var document = new PlanDocument(
            "compound-vehicle-object",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(760, 520),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(80, 80), new PlanPoint(680, 80)),
                        Wall("wall-right", new PlanPoint(680, 80), new PlanPoint(680, 440)),
                        Wall("wall-bottom", new PlanPoint(680, 440), new PlanPoint(80, 440)),
                        Wall("wall-left", new PlanPoint(80, 440), new PlanPoint(80, 80)),
                        RoomText("garage-label", "GARASJE", new PlanRect(325, 130, 72, 16)),
                        Symbol("car-body", "CAR_BODY", "A-VEHICLE-CAR", new PlanRect(250, 245, 110, 42)),
                        Symbol("car-wheel-front-left", "CAR_WHEEL", "A-VEHICLE-CAR", new PlanRect(260, 238, 18, 18)),
                        Symbol("car-wheel-front-right", "CAR_WHEEL", "A-VEHICLE-CAR", new PlanRect(332, 238, 18, 18)),
                        Symbol("car-wheel-back-left", "CAR_WHEEL", "A-VEHICLE-CAR", new PlanRect(260, 276, 18, 18)),
                        Symbol("car-wheel-back-right", "CAR_WHEEL", "A-VEHICLE-CAR", new PlanRect(332, 276, 18, 18))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var vehicleChildren = result.ObjectCandidates
            .Where(candidate => candidate.Category == ObjectCategory.Vehicle)
            .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();
        Assert.True(vehicleChildren.Length >= 5);

        var aggregate = Assert.Single(result.ObjectAggregates, aggregate => aggregate.Category == ObjectCategory.Vehicle);
        Assert.Equal(ObjectCandidateKind.Vehicle, aggregate.Kind);
        Assert.Equal(ObjectRoutingInfluence.RoomUseEvidenceOnly, aggregate.RoutingInfluence);
        Assert.Equal(ObjectStructuralInfluence.None, aggregate.StructuralInfluence);
        Assert.True(aggregate.SuppressChildObjectsForRouting);
        Assert.Equal(RoomUseKind.Parking, aggregate.RoomUseEvidence);
        Assert.False(aggregate.RequiresReview);
        Assert.Equal("car", aggregate.Label);
        Assert.True(aggregate.ChildObjectCount >= 5);
        Assert.All(vehicleChildren, child => Assert.Contains(child.Id, aggregate.ChildObjectIds));
        Assert.Equal(aggregate.ChildObjectCount, aggregate.Composition.Children.Count);
        Assert.Contains(
            aggregate.Composition.CategoryCounts,
            count => count.Value == ObjectCategory.Vehicle.ToString() && count.Count == vehicleChildren.Length);
        Assert.Contains(
            aggregate.Composition.KindCounts,
            count => count.Value == ObjectCandidateKind.Vehicle.ToString() && count.Count == vehicleChildren.Length);
        Assert.Contains(
            aggregate.Composition.SourceKindCounts,
            count => count.Value == ObjectCandidateSourceKind.CadSymbol.ToString() && count.Count == vehicleChildren.Length);
        Assert.All(vehicleChildren, child =>
        {
            var childSummary = Assert.Single(aggregate.Composition.Children, item => item.ObjectId == child.Id);
            Assert.Equal(child.Bounds, childSummary.Bounds);
            Assert.Equal(ObjectCategory.Vehicle, childSummary.Category);
            Assert.Equal(ObjectCandidateSourceKind.CadSymbol, childSummary.SourceKind);
            Assert.Contains(child.SourcePrimitiveIds.Single(), childSummary.SourcePrimitiveIds);
        });
        Assert.Contains("A-VEHICLE-CAR", aggregate.SourceLayers);
        Assert.Contains(aggregate.Evidence, item => item.Contains("semantic room-use evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(aggregate.Evidence, item => item.Contains("room-use evidence Parking", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(aggregate.Evidence, item => item.Contains("child category makeup: Vehicle:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(aggregate.Evidence, item => item.Contains("child source-kind makeup: CadSymbol:", StringComparison.OrdinalIgnoreCase));

        var routingLayer = result.RoutingLayer;
        Assert.NotEmpty(routingLayer.Barriers);
        Assert.All(vehicleChildren, child => Assert.Contains(child.Id, routingLayer.SuppressedObjectCandidateIds));
        Assert.All(vehicleChildren, child => Assert.Contains(child.Id, routingLayer.IgnoredObjectCandidateIds));
        Assert.DoesNotContain(routingLayer.Obstacles, obstacle => vehicleChildren.Any(child => child.Id == obstacle.SourceId));
        Assert.DoesNotContain(routingLayer.Obstacles, obstacle => obstacle.SourceId == aggregate.Id);
        Assert.Equal(vehicleChildren.Length, routingLayer.SuppressedObjects.Count);
        Assert.Equal(vehicleChildren.Length, routingLayer.IgnoredObjects.Count);
        Assert.All(vehicleChildren, child =>
        {
            var suppression = Assert.Single(routingLayer.SuppressedObjects, item => item.ObjectCandidateId == child.Id);
            Assert.Equal(aggregate.Id, suppression.SuppressedByAggregateId);
            Assert.Equal(RoutingSuppressionReason.AggregateRoomUseEvidenceOnly, suppression.Reason);
            Assert.Equal(RoutingSuppressedObjectAction.UseAggregateRoomUseHint, suppression.Action);
            Assert.Null(suppression.ReplacementRoutingObstacleId);
            Assert.Equal($"routing-room-use:{aggregate.Id}", suppression.RoomUseHintId);
            Assert.Equal(ObjectRoutingInfluence.RoomUseEvidenceOnly, suppression.AggregateRoutingInfluence);
            Assert.Equal(ObjectStructuralInfluence.None, suppression.AggregateStructuralInfluence);
            Assert.Equal(ObjectCategory.Vehicle, suppression.CandidateCategory);
            Assert.Equal(child.Bounds, suppression.CandidateBounds);
            Assert.Contains(suppression.Evidence, item => item.Contains("instead of treating this child object as an obstacle", StringComparison.OrdinalIgnoreCase));

            var ignored = Assert.Single(routingLayer.IgnoredObjects, item => item.ObjectCandidateId == child.Id);
            Assert.Equal(RoutingIgnoredObjectReason.SuppressedByAggregate, ignored.Reason);
            Assert.Equal(suppression.Id, ignored.SuppressedObjectId);
            Assert.Equal(aggregate.Id, ignored.SuppressedByAggregateId);
            Assert.Equal($"routing-room-use:{aggregate.Id}", ignored.RoomUseHintId);
            Assert.Equal(ObjectRoutingInfluence.RoomUseEvidenceOnly, ignored.RoutingInfluence);
            Assert.Equal(child.Bounds, ignored.CandidateBounds);
            Assert.Contains(ignored.Evidence, item => item.Contains("ignored as a standalone routing item", StringComparison.OrdinalIgnoreCase));
        });

        var parkingHint = Assert.Single(
            routingLayer.RoomUseHints,
            hint => hint.SourceKind == RoutingSourceKind.ObjectAggregate && hint.SourceId == aggregate.Id);
        Assert.Equal(RoomUseKind.Parking, parkingHint.RoomUseKind);
        Assert.Equal(aggregate.Bounds, parkingHint.Bounds);
        Assert.Contains("car-body", parkingHint.SourcePrimitiveIds);

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var aggregateJson = Assert.Single(parsed.RootElement.GetProperty("objectAggregates").EnumerateArray());
        Assert.Equal("Vehicle", aggregateJson.GetProperty("category").GetString());
        Assert.Equal("Vehicle", aggregateJson.GetProperty("kind").GetString());
        Assert.Equal("RoomUseEvidenceOnly", aggregateJson.GetProperty("routingInfluence").GetString());
        Assert.Equal("None", aggregateJson.GetProperty("structuralInfluence").GetString());
        Assert.True(aggregateJson.GetProperty("suppressChildObjectsForRouting").GetBoolean());
        Assert.Equal("Parking", aggregateJson.GetProperty("roomUseEvidence").GetString());
        Assert.True(aggregateJson.GetProperty("childObjectIds").GetArrayLength() >= 5);
        var compositionJson = aggregateJson.GetProperty("composition");
        Assert.Contains(
            compositionJson.GetProperty("categoryCounts").EnumerateArray(),
            item => item.GetProperty("value").GetString() == "Vehicle"
                && item.GetProperty("count").GetInt32() == vehicleChildren.Length);
        Assert.Contains(
            compositionJson.GetProperty("sourceKindCounts").EnumerateArray(),
            item => item.GetProperty("value").GetString() == "CadSymbol"
                && item.GetProperty("count").GetInt32() == vehicleChildren.Length);
        Assert.Equal(vehicleChildren.Length, compositionJson.GetProperty("children").GetArrayLength());
        Assert.Contains(
            compositionJson.GetProperty("children").EnumerateArray(),
            item => item.GetProperty("objectId").GetString() == vehicleChildren[0].Id
                && item.GetProperty("bounds").ValueKind == JsonValueKind.Object
                && item.GetProperty("sourceKind").GetString() == "CadSymbol");
        Assert.Contains("A-VEHICLE-CAR", aggregateJson.GetProperty("sourceLayers").EnumerateArray().Select(item => item.GetString()));

        var routingJson = parsed.RootElement.GetProperty("routingLayer");
        Assert.True(routingJson.GetProperty("barriers").GetArrayLength() > 0);
        Assert.True(routingJson.GetProperty("suppressedObjectCandidateIds").GetArrayLength() >= 5);
        Assert.True(routingJson.GetProperty("suppressedObjects").GetArrayLength() >= 5);
        Assert.True(routingJson.GetProperty("ignoredObjects").GetArrayLength() >= 5);
        Assert.Contains(
            routingJson.GetProperty("suppressedObjects").EnumerateArray(),
            item => item.GetProperty("suppressedByAggregateId").GetString() == aggregate.Id
                && item.GetProperty("reason").GetString() == "AggregateRoomUseEvidenceOnly"
                && item.GetProperty("action").GetString() == "UseAggregateRoomUseHint"
                && item.GetProperty("roomUseHintId").GetString() == $"routing-room-use:{aggregate.Id}"
                && item.GetProperty("aggregateRoutingInfluence").GetString() == "RoomUseEvidenceOnly"
                && item.GetProperty("candidateCategory").GetString() == "Vehicle");
        Assert.Contains(
            routingJson.GetProperty("ignoredObjects").EnumerateArray(),
            item => item.GetProperty("objectCandidateId").GetString() == vehicleChildren[0].Id
                && item.GetProperty("reason").GetString() == "SuppressedByAggregate"
                && item.GetProperty("suppressedObjectId").GetString() == $"routing-suppression:{vehicleChildren[0].Id}"
                && item.GetProperty("candidateBounds").ValueKind == JsonValueKind.Object);
        Assert.Empty(routingJson.GetProperty("obstacles").EnumerateArray()
            .Where(item => item.GetProperty("sourceId").GetString() == aggregate.Id));
        Assert.Contains(
            routingJson.GetProperty("roomUseHints").EnumerateArray(),
            item => item.GetProperty("sourceKind").GetString() == "ObjectAggregate"
                && item.GetProperty("sourceId").GetString() == aggregate.Id
                && item.GetProperty("roomUseKind").GetString() == "Parking");

        var geoJson = PlanTraceGeoJsonExporter.Serialize(result);
        using var parsedGeoJson = JsonDocument.Parse(geoJson);
        Assert.Contains(
            parsedGeoJson.RootElement.GetProperty("features").EnumerateArray(),
            feature => feature.GetProperty("properties").GetProperty("featureType").GetString() == "objectAggregate"
                && feature.GetProperty("properties").GetProperty("routingInfluence").GetString() == "RoomUseEvidenceOnly");
        Assert.Contains(
            parsedGeoJson.RootElement.GetProperty("features").EnumerateArray(),
            feature => feature.GetProperty("properties").GetProperty("featureType").GetString() == "routingRoomUseHint"
                && feature.GetProperty("properties").GetProperty("sourceId").GetString() == aggregate.Id);
        Assert.Contains(
            parsedGeoJson.RootElement.GetProperty("features").EnumerateArray(),
            feature => feature.GetProperty("properties").GetProperty("featureType").GetString() == "routingSuppressedObject"
                && feature.GetProperty("properties").GetProperty("suppressedByAggregateId").GetString() == aggregate.Id
                && feature.GetProperty("properties").GetProperty("action").GetString() == "UseAggregateRoomUseHint");
        Assert.Contains(
            parsedGeoJson.RootElement.GetProperty("features").EnumerateArray(),
            feature => feature.GetProperty("properties").GetProperty("featureType").GetString() == "routingIgnoredObject"
                && feature.GetProperty("properties").GetProperty("reason").GetString() == "SuppressedByAggregate");

        var placementJson = PlanPlacementJsonExporter.Serialize(result);
        using var parsedPlacement = JsonDocument.Parse(placementJson);
        var placementAggregateJson = Assert.Single(parsedPlacement.RootElement.GetProperty("objectAggregates").EnumerateArray());
        Assert.True(placementAggregateJson.GetProperty("childObjectIds").GetArrayLength() >= 5);
        var placementCompositionJson = placementAggregateJson.GetProperty("composition");
        Assert.Equal(vehicleChildren.Length, placementCompositionJson.GetProperty("children").GetArrayLength());
        Assert.Contains(
            placementCompositionJson.GetProperty("children").EnumerateArray(),
            item => item.GetProperty("objectId").GetString() == vehicleChildren[0].Id
                && item.GetProperty("bounds").ValueKind == JsonValueKind.Object
                && item.GetProperty("center").ValueKind == JsonValueKind.Object
                && item.GetProperty("boundsMillimeters").ValueKind == JsonValueKind.Null);
        Assert.Contains(
            parsedPlacement.RootElement.GetProperty("routingLayer").GetProperty("ignoredObjects").EnumerateArray(),
            item => item.GetProperty("objectCandidateId").GetString() == vehicleChildren[0].Id
                && item.GetProperty("reason").GetString() == "SuppressedByAggregate"
                && item.GetProperty("candidateCenter").ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task ScanAsync_DoesNotReferenceMissingChildRoomUseHintsForAggregateObstacleSuppression()
    {
        var document = new PlanDocument(
            "compound-equipment-object",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(760, 520),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(80, 80), new PlanPoint(680, 80)),
                        Wall("wall-right", new PlanPoint(680, 80), new PlanPoint(680, 440)),
                        Wall("wall-bottom", new PlanPoint(680, 440), new PlanPoint(80, 440)),
                        Wall("wall-left", new PlanPoint(80, 440), new PlanPoint(80, 80)),
                        RoomText("process-label", "PROCESS", new PlanRect(325, 130, 72, 16)),
                        Symbol("pump-a", "PROCESS_PUMP", "I-EQUIP", new PlanRect(250, 245, 26, 26)),
                        Symbol("pump-b", "PROCESS_PUMP", "I-EQUIP", new PlanRect(274, 245, 26, 26)),
                        Symbol("pump-c", "PROCESS_PUMP", "I-EQUIP", new PlanRect(298, 245, 26, 26))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var aggregate = Assert.Single(result.ObjectAggregates, aggregate => aggregate.Category == ObjectCategory.Equipment);
        var equipmentChildren = result.ObjectCandidates
            .Where(candidate => candidate.Category == ObjectCategory.Equipment)
            .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ObjectRoutingInfluence.HardObstacle, aggregate.RoutingInfluence);
        Assert.True(aggregate.SuppressChildObjectsForRouting);
        Assert.All(equipmentChildren, child =>
        {
            var suppression = Assert.Single(result.RoutingLayer.SuppressedObjects, item => item.ObjectCandidateId == child.Id);
            var ignored = Assert.Single(result.RoutingLayer.IgnoredObjects, item => item.ObjectCandidateId == child.Id);

            Assert.Equal(RoutingSuppressedObjectAction.UseAggregateObstacle, suppression.Action);
            Assert.NotNull(suppression.ReplacementRoutingObstacleId);
            Assert.Null(suppression.RoomUseHintId);
            Assert.Equal(RoutingIgnoredObjectReason.SuppressedByAggregate, ignored.Reason);
            Assert.Equal(suppression.Id, ignored.SuppressedObjectId);
            Assert.Null(ignored.RoomUseHintId);
        });
    }

    [Fact]
    public async Task ScanAsync_TreatsMovableFurnitureAggregateAsRoomUseEvidenceOnly()
    {
        var document = new PlanDocument(
            "movable-furniture-room-use-aggregate",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(760, 520),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(80, 80), new PlanPoint(680, 80)),
                        Wall("wall-right", new PlanPoint(680, 80), new PlanPoint(680, 440)),
                        Wall("wall-bottom", new PlanPoint(680, 440), new PlanPoint(80, 440)),
                        Wall("wall-left", new PlanPoint(80, 440), new PlanPoint(80, 80)),
                        RoomText("living-label", "STUE", new PlanRect(330, 130, 54, 16)),
                        Symbol("sofa-1", "SOFA_2P", "A-FURN-MOVEABLE", new PlanRect(250, 245, 52, 28)),
                        Symbol("table-1", "COFFEE_TABLE", "A-FURN-MOVEABLE", new PlanRect(310, 248, 30, 24)),
                        Symbol("chair-1", "LOUNGE_CHAIR", "A-FURN-MOVEABLE", new PlanRect(348, 246, 28, 28))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var furnitureChildren = result.ObjectCandidates
            .Where(candidate => candidate.Category == ObjectCategory.Furniture)
            .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(3, furnitureChildren.Length);

        var aggregate = Assert.Single(result.ObjectAggregates, item => item.Category == ObjectCategory.Furniture);
        Assert.Equal(ObjectCandidateKind.Furniture, aggregate.Kind);
        Assert.Equal(ObjectRoutingInfluence.RoomUseEvidenceOnly, aggregate.RoutingInfluence);
        Assert.Equal(ObjectStructuralInfluence.None, aggregate.StructuralInfluence);
        Assert.Equal(RoomUseKind.Living, aggregate.RoomUseEvidence);
        Assert.True(aggregate.SuppressChildObjectsForRouting);
        Assert.False(aggregate.RequiresReview);
        Assert.Contains(aggregate.Evidence, item => item.Contains("semantic room-use evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(aggregate.Evidence, item => item.Contains("room-use evidence Living", StringComparison.OrdinalIgnoreCase));
        Assert.All(furnitureChildren, child => Assert.Contains(child.Id, aggregate.ChildObjectIds));

        Assert.DoesNotContain(result.RoutingLayer.Obstacles, obstacle => obstacle.SourceId == aggregate.Id);
        Assert.DoesNotContain(result.RoutingLayer.Obstacles, obstacle => furnitureChildren.Any(child => child.Id == obstacle.SourceId));
        Assert.All(furnitureChildren, child =>
        {
            Assert.Contains(child.Id, result.RoutingLayer.SuppressedObjectCandidateIds);
            Assert.Contains(child.Id, result.RoutingLayer.IgnoredObjectCandidateIds);

            var suppression = Assert.Single(result.RoutingLayer.SuppressedObjects, item => item.ObjectCandidateId == child.Id);
            Assert.Equal(aggregate.Id, suppression.SuppressedByAggregateId);
            Assert.Equal(RoutingSuppressionReason.AggregateRoomUseEvidenceOnly, suppression.Reason);
            Assert.Equal(RoutingSuppressedObjectAction.UseAggregateRoomUseHint, suppression.Action);
            Assert.Null(suppression.ReplacementRoutingObstacleId);
            Assert.Equal($"routing-room-use:{aggregate.Id}", suppression.RoomUseHintId);

            var ignored = Assert.Single(result.RoutingLayer.IgnoredObjects, item => item.ObjectCandidateId == child.Id);
            Assert.Equal(RoutingIgnoredObjectReason.SuppressedByAggregate, ignored.Reason);
            Assert.Equal($"routing-room-use:{aggregate.Id}", ignored.RoomUseHintId);
        });

        var furnitureHint = Assert.Single(
            result.RoutingLayer.RoomUseHints,
            hint => hint.SourceKind == RoutingSourceKind.ObjectAggregate && hint.SourceId == aggregate.Id);
        Assert.Equal(RoomUseKind.Living, furnitureHint.RoomUseKind);
        Assert.Equal(aggregate.Bounds, furnitureHint.Bounds);
    }

    [Fact]
    public async Task ScanAsync_KeepsFixedCabinetFurnitureAggregateAsSoftObstacle()
    {
        var document = new PlanDocument(
            "fixed-cabinet-furniture-aggregate",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(760, 520),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(80, 80), new PlanPoint(680, 80)),
                        Wall("wall-right", new PlanPoint(680, 80), new PlanPoint(680, 440)),
                        Wall("wall-bottom", new PlanPoint(680, 440), new PlanPoint(80, 440)),
                        Wall("wall-left", new PlanPoint(80, 440), new PlanPoint(80, 80)),
                        RoomText("kitchen-label", "KITCHEN", new PlanRect(325, 130, 80, 16)),
                        Symbol("cabinet-1", "BASE_CABINET", "A-FURN-CABINET", new PlanRect(250, 245, 32, 28)),
                        Symbol("cabinet-2", "BASE_CABINET", "A-FURN-CABINET", new PlanRect(284, 245, 32, 28)),
                        Symbol("cabinet-3", "BASE_CABINET", "A-FURN-CABINET", new PlanRect(318, 245, 32, 28))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var cabinetChildren = result.ObjectCandidates
            .Where(candidate => candidate.Category == ObjectCategory.Furniture)
            .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(3, cabinetChildren.Length);

        var aggregate = Assert.Single(result.ObjectAggregates, item => item.Category == ObjectCategory.Furniture);
        Assert.Equal(ObjectRoutingInfluence.SoftObstacle, aggregate.RoutingInfluence);
        Assert.Equal(ObjectStructuralInfluence.NonStructural, aggregate.StructuralInfluence);
        Assert.True(aggregate.SuppressChildObjectsForRouting);

        var obstacle = Assert.Single(result.RoutingLayer.Obstacles, obstacle => obstacle.SourceId == aggregate.Id);
        Assert.Equal(RoutingObstacleKind.SoftObstacle, obstacle.ObstacleKind);
        Assert.Equal(ObjectRoutingInfluence.SoftObstacle, obstacle.RoutingInfluence);

        Assert.All(cabinetChildren, child =>
        {
            var suppression = Assert.Single(result.RoutingLayer.SuppressedObjects, item => item.ObjectCandidateId == child.Id);
            Assert.Equal(RoutingSuppressedObjectAction.UseAggregateObstacle, suppression.Action);
            Assert.Equal($"routing-obstacle:{aggregate.Id}", suppression.ReplacementRoutingObstacleId);
            Assert.Null(suppression.RoomUseHintId);
            Assert.DoesNotContain(result.RoutingLayer.Obstacles, obstacle => obstacle.SourceId == child.Id);
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

    private static LinePrimitive EquipmentLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "I-EQUIP-VALVE",
            Source = Source(sourceId, "LINE", "I-EQUIP-VALVE")
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
