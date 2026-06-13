public sealed class LayerCategoryProfileTests
{
    [Fact]
    public void ParseJson_ReadsLayerCategoryOverrides()
    {
        const string json = """
            {
              "schemaVersion": "openplantrace.layer-profile.v1",
              "name": "Industrial standard",
              "version": "2026.1",
              "overrides": [
                { "pattern": "XREF-*-LINEWORK", "category": "Wall", "sourceFormat": "dxf" },
                { "pattern": "E-EQP-*", "category": "Electrical" }
              ]
            }
            """;

        var profile = LayerCategoryProfile.ParseJson(json);

        Assert.Equal(LayerCategoryProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Equal("Industrial standard", profile.Name);
        Assert.Equal("2026.1", profile.Version);
        Assert.Equal(2, profile.Overrides.Count);
        Assert.Equal("XREF-*-LINEWORK", profile.Overrides[0].Pattern);
        Assert.Equal(LayerCategory.Wall, profile.Overrides[0].Category);
        Assert.Equal("dxf", profile.Overrides[0].SourceFormat);
        Assert.Equal(LayerCategory.Electrical, profile.Overrides[1].Category);
    }

    [Fact]
    public void ParseJson_RejectsInvalidCategory()
    {
        const string json = """
            {
              "schemaVersion": "openplantrace.layer-profile.v1",
              "overrides": [
                { "pattern": "BAD-*", "category": "DefinitelyNotReal" }
              ]
            }
            """;

        var exception = Assert.Throws<ArgumentException>(() => LayerCategoryProfile.ParseJson(json));
        Assert.Contains("invalid category", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseJson_RejectsUnsupportedSchemaVersion()
    {
        const string json = """
            {
              "schemaVersion": "openplantrace.layer-profile.v99",
              "overrides": []
            }
            """;

        var exception = Assert.Throws<ArgumentException>(() => LayerCategoryProfile.ParseJson(json));
        Assert.Contains("Unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_UsesParsedProfileOverridesWithSourceFormatScope()
    {
        const string json = """
            {
              "schemaVersion": "openplantrace.layer-profile.v1",
              "overrides": [
                { "pattern": "MODEL-WALL-*", "category": "Wall", "sourceFormat": "dxf" },
                { "pattern": "MODEL-WALL-*", "category": "Grid", "sourceFormat": "pdf" }
              ]
            }
            """;

        var profile = LayerCategoryProfile.ParseJson(json);
        var document = new PlanDocument(
            "profile-source-format",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        new LinePrimitive(
                            new PlanLineSegment(new PlanPoint(20, 20), new PlanPoint(280, 20)))
                        {
                            Layer = "MODEL-WALL-A",
                            Source = new PrimitiveSourceMetadata
                            {
                                SourceId = "dxf-wall",
                                SourceFormat = "dxf",
                                EntityType = "LINE",
                                Layer = "MODEL-WALL-A"
                            }
                        },
                        new LinePrimitive(
                            new PlanLineSegment(new PlanPoint(20, 80), new PlanPoint(280, 80)))
                        {
                            Layer = "MODEL-WALL-A",
                            Source = new PrimitiveSourceMetadata
                            {
                                SourceId = "pdf-grid",
                                SourceFormat = "pdf",
                                EntityType = "PATH",
                                Layer = "MODEL-WALL-A"
                            }
                        }
                    })
            });

        var analysis = LayerAnalyzer.Analyze(document, profile.Overrides);

        Assert.Equal(LayerCategory.Wall, analysis.Find("MODEL-WALL-A", "dxf")!.LikelyCategory);
        Assert.Equal(LayerCategory.Grid, analysis.Find("MODEL-WALL-A", "pdf")!.LikelyCategory);
    }
}
