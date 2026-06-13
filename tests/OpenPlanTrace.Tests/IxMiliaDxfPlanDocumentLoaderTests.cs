using System.Text;

namespace OpenPlanTrace.Tests;

public sealed class IxMiliaDxfPlanDocumentLoaderTests
{
    [Fact]
    public async Task LoadAsync_ExtractsCadEntitiesIntoPlanPrimitives()
    {
        var loader = new IxMiliaDxfPlanDocumentLoader();
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(CreateMinimalDxf()));

        var document = await loader.LoadAsync(
            stream,
            PlanSourceDescriptor.FromFileNameOrExtension(".dxf"));

        var page = Assert.Single(document.Pages);

        Assert.Contains(page.Primitives, primitive => primitive is LinePrimitive);
        Assert.Contains(page.Primitives, primitive => primitive is PolylinePrimitive { Closed: true });
        Assert.Contains(page.Primitives, primitive => primitive is TextPrimitive text && text.Text == "ROOM");
        Assert.Contains(page.Primitives, primitive => primitive is SymbolPrimitive symbol && symbol.Name == "DOOR");
        Assert.Equal("dxf", document.Metadata.Properties["format"]);
        Assert.Equal("Dxf", document.Metadata.Properties["sourceKind"]);
        Assert.Equal("Dxf", document.Metadata.Properties["effectiveSourceKind"]);

        var line = page.Primitives.OfType<LinePrimitive>().First();
        Assert.Equal("dxf", line.Source.SourceFormat);
        Assert.Equal("LINE", line.Source.EntityType);
        Assert.Equal("WALLS", line.Source.Layer);
        Assert.Equal(SourceDrawingSpace.Model, line.Source.DrawingSpace);

        var symbol = page.Primitives.OfType<SymbolPrimitive>().First();
        Assert.Equal("DOOR", symbol.Source.BlockName);
        Assert.Equal("SYMBOLS", symbol.Source.Layer);
        Assert.Equal("INSERT", symbol.Source.EntityType);
    }

    [Fact]
    public async Task LoadAsync_ExpandsBlockInsertGeometryWithInheritedLayerAndProvenance()
    {
        var loader = new IxMiliaDxfPlanDocumentLoader();
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(CreateBlockInsertDxf()));

        var document = await loader.LoadAsync(
            stream,
            PlanSourceDescriptor.FromFileNameOrExtension(".dxf"));

        var page = Assert.Single(document.Pages);
        var symbol = Assert.Single(page.Primitives.OfType<SymbolPrimitive>());
        var expandedLine = Assert.Single(page.Primitives.OfType<LinePrimitive>());
        var expandedArc = Assert.Single(page.Primitives.OfType<ArcPrimitive>());

        Assert.Equal("DOOR_SWING", symbol.Name);
        Assert.Equal("1", document.Metadata.Properties["dxf.blockCount"]);
        Assert.Equal("2", document.Metadata.Properties["dxf.expandedBlockPrimitiveCount"]);

        Assert.Equal("A-DOOR", expandedLine.Layer);
        Assert.Equal("A-DOOR", expandedLine.Source.Layer);
        Assert.Equal("DOOR_SWING", expandedLine.Source.BlockName);
        Assert.Equal("LINE", expandedLine.Source.EntityType);
        Assert.Equal("true", expandedLine.Source.Properties["expandedFromBlock"]);
        Assert.Contains("parentInsertSourceId", expandedLine.Source.Properties.Keys);
        Assert.Equal("0", expandedLine.Source.Properties["blockEntityLayer"]);

        Assert.Equal("A-DOOR", expandedArc.Layer);
        Assert.Equal("DOOR_SWING", expandedArc.Source.BlockName);
        Assert.Equal("ARC", expandedArc.Source.EntityType);
        Assert.True(expandedArc.Radius > 50);
        Assert.True(expandedLine.Segment.Length > 50);
    }

    [Fact]
    public async Task LoadAsync_ExtractsInsertAttributesAsTextPrimitivesWithProvenance()
    {
        var loader = new IxMiliaDxfPlanDocumentLoader();
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(CreateInsertAttributeDxf()));

        var document = await loader.LoadAsync(
            stream,
            PlanSourceDescriptor.FromFileNameOrExtension(".dxf"));

        var page = Assert.Single(document.Pages);
        var text = Assert.Single(page.Primitives.OfType<TextPrimitive>(), primitive => primitive.Text == "PUMP-101");

        Assert.Equal("1", document.Metadata.Properties["dxf.insertAttributePrimitiveCount"]);
        Assert.Equal("A-EQPM-TEXT", text.Layer);
        Assert.Equal("ATTRIB", text.Source.EntityType);
        Assert.Equal("PUMP", text.Source.BlockName);
        Assert.Equal("true", text.Source.Properties["insertAttribute"]);
        Assert.Equal("TAG", text.Source.Properties["attributeTag"]);
        Assert.Equal("PUMP-101", text.Source.Properties["attributeValue"]);
        Assert.Contains(":attrib:1:TAG", text.SourceId);
        Assert.Contains("parentInsertSourceId", text.Source.Properties.Keys);
    }

    [Fact]
    public async Task ScanAsync_UsesExpandedBlockArcForDoorOpeningClassification()
    {
        var loader = new IxMiliaDxfPlanDocumentLoader();
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(CreateBlockDoorOpeningDxf()));
        var document = await loader.LoadAsync(
            stream,
            PlanSourceDescriptor.FromFileNameOrExtension(".dxf"));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var opening = Assert.Single(result.Openings);

        Assert.Equal(OpeningType.Door, opening.Type);
        Assert.Equal(OpeningOperation.Hinged, opening.Operation);
        Assert.Contains(opening.SourcePrimitiveIds, sourceId => sourceId.Contains(":block:DOOR-SWING:", StringComparison.Ordinal));
        Assert.Contains(opening.Evidence, item => item.Contains("door swing arc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_UsesInsertAttributeAsRoomLabel()
    {
        var loader = new IxMiliaDxfPlanDocumentLoader();
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(CreateRoomAttributeDxf()));
        var document = await loader.LoadAsync(
            stream,
            PlanSourceDescriptor.FromFileNameOrExtension(".dxf"));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var room = Assert.Single(result.Rooms);

        Assert.Equal("OFFICE", room.Label);
        Assert.Contains(room.LabelSourcePrimitiveIds, sourceId => sourceId.Contains(":attrib:1:ROOM-NAME", StringComparison.Ordinal));
        Assert.Contains(room.Evidence, item => item.Contains("OFFICE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadAsync_ExtractsLinearDimensionEntityAsTextAndLinePrimitives()
    {
        var loader = new IxMiliaDxfPlanDocumentLoader();
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(CreateDimensionEntityDxf()));

        var document = await loader.LoadAsync(
            stream,
            PlanSourceDescriptor.FromFileNameOrExtension(".dxf"));

        var page = Assert.Single(document.Pages);
        var text = Assert.Single(page.Primitives.OfType<TextPrimitive>(), primitive => primitive.Source.Properties.TryGetValue("dimensionPrimitiveKind", out var kind) && kind == "text");
        var line = Assert.Single(page.Primitives.OfType<LinePrimitive>(), primitive => primitive.Source.Properties.TryGetValue("dimensionPrimitiveKind", out var kind) && kind == "line");

        Assert.Equal("2", document.Metadata.Properties["dxf.dimensionPrimitiveCount"]);
        Assert.Equal("3000 mm", text.Text);
        Assert.Equal("A-DIM", text.Layer);
        Assert.Equal("DIMENSION", text.Source.EntityType);
        Assert.Equal("text", text.Source.Properties["dimensionPrimitiveKind"]);
        Assert.Equal("RotatedHorizontalOrVertical", text.Source.Properties["dimensionType"]);
        Assert.Equal("line", line.Source.Properties["dimensionPrimitiveKind"]);
        Assert.True(line.Segment.Length > 1000);
    }

    [Fact]
    public async Task ScanAsync_UsesDxfDimensionEntityForMatchedDimensionAnnotation()
    {
        var loader = new IxMiliaDxfPlanDocumentLoader();
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(CreateDimensionEntityDxf()));
        var document = await loader.LoadAsync(
            stream,
            PlanSourceDescriptor.FromFileNameOrExtension(".dxf"));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var dimension = Assert.Single(result.Dimensions);

        Assert.Equal("3000 mm", dimension.NormalizedText);
        Assert.Equal(DimensionOrientation.Horizontal, dimension.Orientation);
        Assert.NotNull(dimension.DimensionLine);
        Assert.Contains(dimension.SourcePrimitiveIds, sourceId => sourceId.Contains(":dimension:text", StringComparison.Ordinal));
        Assert.Contains(dimension.SourcePrimitiveIds, sourceId => sourceId.Contains(":dimension:line", StringComparison.Ordinal));
    }

    private static string CreateMinimalDxf() =>
        """
        0
        SECTION
        2
        ENTITIES
        0
        LINE
        8
        WALLS
        10
        0
        20
        0
        11
        100
        21
        0
        0
        LWPOLYLINE
        8
        ROOMS
        90
        4
        70
        1
        10
        0
        20
        0
        10
        100
        20
        0
        10
        100
        20
        80
        10
        0
        20
        80
        0
        TEXT
        8
        LABELS
        10
        15
        20
        35
        40
        8
        1
        ROOM
        0
        INSERT
        8
        SYMBOLS
        2
        DOOR
        10
        50
        20
        0
        0
        ENDSEC
        0
        EOF
        """;

    private static string CreateBlockInsertDxf() =>
        """
        0
        SECTION
        2
        BLOCKS
        0
        BLOCK
        8
        0
        2
        DOOR_SWING
        70
        0
        10
        0
        20
        0
        0
        LINE
        8
        0
        10
        0
        20
        0
        11
        30
        21
        0
        0
        ARC
        8
        0
        10
        0
        20
        0
        40
        30
        50
        0
        51
        90
        0
        ENDBLK
        0
        ENDSEC
        0
        SECTION
        2
        ENTITIES
        0
        INSERT
        8
        A-DOOR
        2
        DOOR_SWING
        10
        100
        20
        50
        41
        2
        42
        2
        50
        90
        0
        ENDSEC
        0
        EOF
        """;

    private static string CreateBlockDoorOpeningDxf() =>
        """
        0
        SECTION
        2
        BLOCKS
        0
        BLOCK
        8
        0
        2
        DOOR_SWING
        70
        0
        10
        0
        20
        0
        0
        ARC
        8
        0
        10
        0
        20
        0
        40
        30
        50
        0
        51
        90
        0
        ENDBLK
        0
        ENDSEC
        0
        SECTION
        2
        ENTITIES
        0
        LINE
        8
        A-WALL
        10
        0
        20
        0
        11
        100
        21
        0
        0
        LINE
        8
        A-WALL
        10
        130
        20
        0
        11
        260
        21
        0
        0
        INSERT
        8
        A-DOOR
        2
        DOOR_SWING
        10
        100
        20
        0
        0
        ENDSEC
        0
        EOF
        """;

    private static string CreateInsertAttributeDxf() =>
        """
        0
        SECTION
        2
        BLOCKS
        0
        BLOCK
        8
        0
        2
        PUMP
        70
        0
        10
        0
        20
        0
        0
        ENDBLK
        0
        ENDSEC
        0
        SECTION
        2
        ENTITIES
        0
        INSERT
        8
        A-EQPM
        2
        PUMP
        10
        50
        20
        40
        66
        1
        0
        ATTRIB
        8
        A-EQPM-TEXT
        10
        55
        20
        44
        40
        5
        1
        PUMP-101
        2
        TAG
        70
        0
        0
        SEQEND
        0
        ENDSEC
        0
        EOF
        """;

    private static string CreateRoomAttributeDxf() =>
        """
        0
        SECTION
        2
        BLOCKS
        0
        BLOCK
        8
        0
        2
        ROOMTAG
        70
        0
        10
        0
        20
        0
        0
        ENDBLK
        0
        ENDSEC
        0
        SECTION
        2
        ENTITIES
        0
        LINE
        8
        A-WALL
        10
        0
        20
        0
        11
        100
        21
        0
        0
        LINE
        8
        A-WALL
        10
        100
        20
        0
        11
        100
        21
        80
        0
        LINE
        8
        A-WALL
        10
        100
        20
        80
        11
        0
        21
        80
        0
        LINE
        8
        A-WALL
        10
        0
        20
        80
        11
        0
        21
        0
        0
        INSERT
        8
        A-ROOM-NAME
        2
        ROOMTAG
        10
        45
        20
        40
        66
        1
        0
        ATTRIB
        8
        A-ROOM-NAME
        10
        45
        20
        40
        40
        6
        1
        OFFICE
        2
        ROOM-NAME
        70
        0
        0
        SEQEND
        0
        ENDSEC
        0
        EOF
        """;

    private static string CreateDimensionEntityDxf() =>
        """
        0
        SECTION
        2
        ENTITIES
        0
        LINE
        8
        A-WALL
        10
        0
        20
        0
        11
        3000
        21
        0
        0
        DIMENSION
        8
        A-DIM
        10
        1500
        20
        120
        11
        1500
        21
        145
        13
        0
        23
        0
        14
        3000
        24
        0
        50
        0
        70
        0
        1
        3000 mm
        3
        STANDARD
        0
        ENDSEC
        0
        EOF
        """;

}
