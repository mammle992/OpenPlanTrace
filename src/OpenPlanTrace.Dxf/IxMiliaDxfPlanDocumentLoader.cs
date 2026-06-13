using IxMilia.Dxf;
using IxMilia.Dxf.Blocks;
using IxMilia.Dxf.Entities;
using System.Globalization;

namespace OpenPlanTrace.Dxf;

public sealed class IxMiliaDxfPlanDocumentLoader : PlanDocumentLoaderBase
{
    private const double PageMargin = 20.0;
    private const int MaxBlockExpansionDepth = 8;

    public IxMiliaDxfPlanDocumentLoader()
        : base("DXF/IxMilia", PlanSourceKind.Dxf)
    {
    }

    public override ValueTask<PlanDocument> LoadAsync(
        Stream stream,
        PlanSourceDescriptor source,
        PlanLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(source);

        var dxf = DxfFile.Load(stream);
        var documentSource = new DocumentSourceInfo(
            source.Name ?? source.FilePath ?? "dxf-document",
            source.Name,
            source.FilePath,
            dxf.Header.DefaultDrawingUnits);
        var rawPrimitives = ExtractRawPrimitives(dxf, documentSource, cancellationToken).ToArray();
        var rawBounds = PlanRect.Union(rawPrimitives.Select(primitive => primitive.Bounds));

        if (rawBounds.IsEmpty)
        {
            rawBounds = new PlanRect(0, 0, 1, 1);
        }

        var transform = new DxfDrawingTransform(rawBounds, PageMargin);
        var primitives = rawPrimitives
            .Select(primitive => primitive.Create(transform))
            .Where(primitive => !primitive.Bounds.IsEmpty)
            .ToArray();

        var page = new PlanPage(
            1,
            new PlanSize(transform.PageWidth, transform.PageHeight),
            primitives);

        var document = new PlanDocument(
            source.Name ?? source.FilePath ?? "dxf-document",
            new[] { page })
        {
            Metadata = new PlanMetadata
            {
                SourceName = source.Name,
                SourcePath = source.FilePath,
                Properties = CreateMetadataProperties(source, dxf, rawPrimitives)
            }
        };

        return ValueTask.FromResult(document);
    }

    private IReadOnlyDictionary<string, string> CreateMetadataProperties(
        PlanSourceDescriptor source,
        DxfFile dxf,
        IReadOnlyList<RawPrimitive> rawPrimitives)
    {
        var properties = new Dictionary<string, string>
        {
            ["format"] = "dxf",
            ["loader"] = FormatName,
            ["sourceKind"] = source.Kind.ToString(),
            ["effectiveSourceKind"] = source.EffectiveKind.ToString(),
            ["entityCount"] = dxf.Entities.Count.ToString(),
            ["layerCount"] = dxf.Layers.Count.ToString(),
            ["dxf.blockCount"] = dxf.Blocks.Count.ToString(CultureInfo.InvariantCulture),
            ["dxf.expandedBlockPrimitiveCount"] = rawPrimitives.Count(primitive => primitive.ExpandedFromBlock).ToString(CultureInfo.InvariantCulture),
            ["dxf.insertAttributePrimitiveCount"] = rawPrimitives.Count(primitive => primitive.FromInsertAttribute).ToString(CultureInfo.InvariantCulture),
            ["dxf.dimensionPrimitiveCount"] = rawPrimitives.Count(primitive => primitive.FromDimensionEntity).ToString(CultureInfo.InvariantCulture),
            ["coordinateOrigin"] = "normalized-top-left",
            ["dxf.defaultDrawingUnits"] = dxf.Header.DefaultDrawingUnits.ToString(),
            ["dxf.drawingUnits"] = dxf.Header.DrawingUnits.ToString(),
            ["dxf.unitFormat"] = dxf.Header.UnitFormat.ToString(),
            ["dxf.unitPrecision"] = dxf.Header.UnitPrecision.ToString(CultureInfo.InvariantCulture),
            ["dxf.dimensionLinearScaleFactor"] = dxf.Header.DimensionLinearMeasurementsScaleFactor.ToString(CultureInfo.InvariantCulture),
            ["dxf.dimensioningScaleFactor"] = dxf.Header.DimensioningScaleFactor.ToString(CultureInfo.InvariantCulture)
        };

        AddSourceDescriptorProperties(properties, source);
        return properties;
    }

    private static void AddSourceDescriptorProperties(
        IDictionary<string, string> properties,
        PlanSourceDescriptor source)
    {
        Add(properties, "fileExtension", source.FileExtension);
        Add(properties, "contentType", source.ContentType);

        if (source.ClipboardContentKind is { } clipboardContentKind)
        {
            properties["clipboardContentKind"] = clipboardContentKind.ToString();
        }

        foreach (var (key, value) in source.Properties)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                properties[$"source.{key.Trim()}"] = value.Trim();
            }
        }
    }

    private static void Add(IDictionary<string, string> properties, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            properties[key] = value.Trim();
        }
    }

    private static IEnumerable<RawPrimitive> ExtractRawPrimitives(
        DxfFile dxf,
        DocumentSourceInfo documentSource,
        CancellationToken cancellationToken)
    {
        var index = 0;
        var blockLookup = dxf.Blocks
            .Where(block => !string.IsNullOrWhiteSpace(block.Name))
            .GroupBy(block => block.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var entity in dxf.Entities)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!entity.IsVisible)
            {
                continue;
            }

            index++;
            var sourceId = $"dxf:entity:{index}:{entity.EntityTypeString}";

            foreach (var primitive in ExtractEntity(
                entity,
                sourceId,
                documentSource,
                blockLookup,
                EntityExtractionOptions.Root,
                depth: 0,
                visitedBlocks: new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            {
                yield return primitive;
            }
        }
    }

    private static IEnumerable<RawPrimitive> ExtractEntity(
        DxfEntity entity,
        string sourceId,
        DocumentSourceInfo documentSource,
        IReadOnlyDictionary<string, DxfBlock> blockLookup,
        EntityExtractionOptions options,
        int depth,
        HashSet<string> visitedBlocks)
    {
        var effectiveLayer = EffectiveLayer(entity.Layer, options.InheritedLayer);

        switch (entity)
        {
            case DxfLine line:
                yield return CreateLine(line, sourceId, documentSource, options, effectiveLayer);
                break;

            case DxfLwPolyline polyline:
                yield return CreateLightweightPolyline(polyline, sourceId, documentSource, options, effectiveLayer);
                break;

            case DxfPolyline polyline:
                yield return CreatePolyline(polyline, sourceId, documentSource, options, effectiveLayer);
                break;

            case DxfText text:
                yield return CreateText(text, sourceId, documentSource, options, effectiveLayer);
                break;

            case DxfMText text:
                yield return CreateMText(text, sourceId, documentSource, options, effectiveLayer);
                break;

            case DxfArc arc:
                yield return CreateArc(arc, sourceId, documentSource, options, effectiveLayer);
                break;

            case DxfCircle circle:
                yield return CreateCircle(circle, sourceId, documentSource, options, effectiveLayer);
                break;

            case DxfDimensionBase dimension:
                foreach (var primitive in CreateDimensionPrimitives(dimension, sourceId, documentSource, options, effectiveLayer))
                {
                    yield return primitive;
                }

                break;

            case DxfInsert insert:
                yield return CreateInsert(insert, sourceId, documentSource, options, effectiveLayer);

                foreach (var attribute in ExtractInsertAttributes(insert, sourceId, documentSource, options, effectiveLayer))
                {
                    yield return attribute;
                }

                foreach (var child in ExpandInsert(
                    insert,
                    sourceId,
                    documentSource,
                    blockLookup,
                    options,
                    effectiveLayer,
                    depth,
                    visitedBlocks))
                {
                    yield return child;
                }
                break;
        }
    }

    private static IEnumerable<RawPrimitive> ExtractInsertAttributes(
        DxfInsert insert,
        string sourceId,
        DocumentSourceInfo documentSource,
        EntityExtractionOptions options,
        string insertLayer)
    {
        for (var index = 0; index < insert.Attributes.Count; index++)
        {
            var attribute = insert.Attributes[index];
            if (!attribute.IsVisible
                || attribute.IsInvisible
                || string.IsNullOrWhiteSpace(AttributeText(attribute)))
            {
                continue;
            }

            var attributeSourceId = $"{sourceId}:attrib:{index + 1}:{SafeSourceSegment(attribute.AttributeTag ?? "attribute")}";
            yield return CreateAttributeText(
                attribute,
                attributeSourceId,
                documentSource,
                options,
                insertLayer,
                insert.Name,
                sourceId);
        }
    }

    private static IEnumerable<RawPrimitive> ExpandInsert(
        DxfInsert insert,
        string sourceId,
        DocumentSourceInfo documentSource,
        IReadOnlyDictionary<string, DxfBlock> blockLookup,
        EntityExtractionOptions options,
        string effectiveLayer,
        int depth,
        HashSet<string> visitedBlocks)
    {
        if (depth >= MaxBlockExpansionDepth
            || string.IsNullOrWhiteSpace(insert.Name)
            || !blockLookup.TryGetValue(insert.Name, out var block)
            || !visitedBlocks.Add(block.Name))
        {
            yield break;
        }

        try
        {
            var rowCount = Math.Max(1, (int)insert.RowCount);
            var columnCount = Math.Max(1, (int)insert.ColumnCount);
            var childIndex = 0;

            for (var row = 0; row < rowCount; row++)
            {
                for (var column = 0; column < columnCount; column++)
                {
                    var insertTransform = options.Transform.Compose(
                        RawEntityTransform.FromInsert(insert, RawPoint(block.BasePoint), row, column));
                    var expansionProperties = new Dictionary<string, string>
                    {
                        ["expandedFromBlock"] = "true",
                        ["parentInsertSourceId"] = sourceId,
                        ["parentInsertName"] = insert.Name,
                        ["blockName"] = block.Name,
                        ["blockBasePointX"] = block.BasePoint.X.ToString(CultureInfo.InvariantCulture),
                        ["blockBasePointY"] = block.BasePoint.Y.ToString(CultureInfo.InvariantCulture),
                        ["insertRow"] = row.ToString(CultureInfo.InvariantCulture),
                        ["insertColumn"] = column.ToString(CultureInfo.InvariantCulture),
                        ["insertRotation"] = insert.Rotation.ToString(CultureInfo.InvariantCulture),
                        ["insertXScaleFactor"] = insert.XScaleFactor.ToString(CultureInfo.InvariantCulture),
                        ["insertYScaleFactor"] = insert.YScaleFactor.ToString(CultureInfo.InvariantCulture)
                    };

                    var childOptions = new EntityExtractionOptions(
                        insertTransform,
                        effectiveLayer,
                        block.Name,
                        sourceId,
                        true,
                        expansionProperties);

                    foreach (var childEntity in block.Entities)
                    {
                        if (!childEntity.IsVisible)
                        {
                            continue;
                        }

                        childIndex++;
                        var childSourceId = $"{sourceId}:block:{SafeSourceSegment(block.Name)}:{row + 1}:{column + 1}:{childIndex}:{childEntity.EntityTypeString}";

                        foreach (var child in ExtractEntity(
                            childEntity,
                            childSourceId,
                            documentSource,
                            blockLookup,
                            childOptions,
                            depth + 1,
                            visitedBlocks))
                        {
                            yield return child;
                        }
                    }
                }
            }
        }
        finally
        {
            visitedBlocks.Remove(block.Name);
        }
    }

    private static RawPrimitive CreateLine(
        DxfLine line,
        string sourceId,
        DocumentSourceInfo documentSource,
        EntityExtractionOptions options,
        string effectiveLayer)
    {
        var start = options.Transform.Point(RawPoint(line.P1));
        var end = options.Transform.Point(RawPoint(line.P2));
        var bounds = PlanRect.FromPoints(start, end);

        return new RawPrimitive(
            bounds,
            transform => new LinePrimitive(new PlanLineSegment(transform.Point(start), transform.Point(end)))
            {
                SourceId = sourceId,
                Layer = effectiveLayer,
                StrokeWidth = StrokeWidth(line) * options.Transform.LengthScale,
                Source = CreateEntitySource(documentSource, line, sourceId, options, effectiveLayer)
            },
            options.ExpandedFromBlock);
    }

    private static RawPrimitive CreateLightweightPolyline(
        DxfLwPolyline polyline,
        string sourceId,
        DocumentSourceInfo documentSource,
        EntityExtractionOptions options,
        string effectiveLayer)
    {
        var points = polyline.Vertices
            .Select(vertex => options.Transform.Point(new PlanPoint(vertex.X, vertex.Y)))
            .ToArray();
        var bounds = BoundsForPoints(points);

        return new RawPrimitive(
            bounds,
            transform => new PolylinePrimitive(points.Select(transform.Point).ToArray(), polyline.IsClosed)
            {
                SourceId = sourceId,
                Layer = effectiveLayer,
                StrokeWidth = (polyline.ConstantWidth > 0 ? polyline.ConstantWidth : StrokeWidth(polyline)) * options.Transform.LengthScale,
                Source = CreateEntitySource(
                    documentSource,
                    polyline,
                    sourceId,
                    options,
                    effectiveLayer,
                    lineWeightOverride: polyline.ConstantWidth > 0 ? polyline.ConstantWidth : null,
                    properties: new Dictionary<string, string>
                    {
                        ["isClosed"] = polyline.IsClosed.ToString(),
                        ["vertexCount"] = polyline.Vertices.Count.ToString()
                    })
            },
            options.ExpandedFromBlock);
    }

    private static RawPrimitive CreatePolyline(
        DxfPolyline polyline,
        string sourceId,
        DocumentSourceInfo documentSource,
        EntityExtractionOptions options,
        string effectiveLayer)
    {
        var points = polyline.Vertices
            .Select(vertex => options.Transform.Point(RawPoint(vertex.Location)))
            .ToArray();
        var bounds = BoundsForPoints(points);

        return new RawPrimitive(
            bounds,
            transform => new PolylinePrimitive(points.Select(transform.Point).ToArray(), polyline.IsClosed)
            {
                SourceId = sourceId,
                Layer = effectiveLayer,
                StrokeWidth = (polyline.DefaultStartingWidth > 0 ? polyline.DefaultStartingWidth : StrokeWidth(polyline)) * options.Transform.LengthScale,
                Source = CreateEntitySource(
                    documentSource,
                    polyline,
                    sourceId,
                    options,
                    effectiveLayer,
                    lineWeightOverride: polyline.DefaultStartingWidth > 0 ? polyline.DefaultStartingWidth : null,
                    properties: new Dictionary<string, string>
                    {
                        ["isClosed"] = polyline.IsClosed.ToString(),
                        ["vertexCount"] = polyline.Vertices.Count.ToString(),
                        ["is3DPolyline"] = polyline.Is3DPolyline.ToString()
                    })
            },
            options.ExpandedFromBlock);
    }

    private static RawPrimitive CreateText(
        DxfText text,
        string sourceId,
        DocumentSourceInfo documentSource,
        EntityExtractionOptions options,
        string effectiveLayer)
    {
        var height = Math.Max(text.TextHeight, 1);
        var width = Math.Max(height, (text.Value?.Length ?? 1) * height * 0.6);
        var location = RawPoint(text.Location);
        var bounds = options.Transform.Rect(new PlanRect(location.X, location.Y, width, height));

        return new RawPrimitive(
            bounds,
            transform => new TextPrimitive(text.Value ?? string.Empty, transform.Rect(bounds))
            {
                SourceId = sourceId,
                Layer = effectiveLayer,
                FontSize = height * options.Transform.LengthScale,
                StrokeWidth = StrokeWidth(text) * options.Transform.LengthScale,
                Source = CreateEntitySource(
                    documentSource,
                    text,
                    sourceId,
                    options,
                    effectiveLayer,
                    properties: new Dictionary<string, string>
                    {
                        ["textHeight"] = text.TextHeight.ToString(CultureInfo.InvariantCulture),
                        ["textStyleName"] = text.TextStyleName,
                        ["rotation"] = (text.Rotation + options.Transform.RotationDegrees).ToString(CultureInfo.InvariantCulture)
                    })
            },
            options.ExpandedFromBlock);
    }

    private static RawPrimitive CreateMText(
        DxfMText text,
        string sourceId,
        DocumentSourceInfo documentSource,
        EntityExtractionOptions options,
        string effectiveLayer)
    {
        var height = Math.Max(text.DefinedHeight > 0 ? text.DefinedHeight : text.InitialTextHeight, 1);
        var width = Math.Max(text.ReferenceRectangleWidth, (text.Text?.Length ?? 1) * height * 0.5);
        var location = RawPoint(text.InsertionPoint);
        var bounds = options.Transform.Rect(new PlanRect(location.X, location.Y, width, height));

        return new RawPrimitive(
            bounds,
            transform => new TextPrimitive(text.Text ?? string.Empty, transform.Rect(bounds))
            {
                SourceId = sourceId,
                Layer = effectiveLayer,
                FontSize = height * options.Transform.LengthScale,
                StrokeWidth = StrokeWidth(text) * options.Transform.LengthScale,
                Source = CreateEntitySource(
                    documentSource,
                    text,
                    sourceId,
                    options,
                    effectiveLayer,
                    properties: new Dictionary<string, string>
                    {
                        ["initialTextHeight"] = text.InitialTextHeight.ToString(CultureInfo.InvariantCulture),
                        ["definedHeight"] = text.DefinedHeight.ToString(CultureInfo.InvariantCulture),
                        ["referenceRectangleWidth"] = text.ReferenceRectangleWidth.ToString(CultureInfo.InvariantCulture),
                        ["textStyleName"] = text.TextStyleName
                    })
            },
            options.ExpandedFromBlock);
    }

    private static RawPrimitive CreateAttributeText(
        DxfAttribute attribute,
        string sourceId,
        DocumentSourceInfo documentSource,
        EntityExtractionOptions options,
        string insertLayer,
        string parentInsertName,
        string parentInsertSourceId)
    {
        var value = AttributeText(attribute);
        var effectiveLayer = EffectiveLayer(attribute.Layer, insertLayer);
        var height = Math.Max(attribute.TextHeight, 1);
        var width = Math.Max(height, value.Length * height * 0.6);
        var location = RawPoint(attribute.Location);
        var bounds = options.Transform.Rect(new PlanRect(location.X, location.Y, width, height));
        var properties = new Dictionary<string, string>
        {
            ["insertAttribute"] = "true",
            ["attributeTag"] = attribute.AttributeTag ?? string.Empty,
            ["attributeValue"] = value,
            ["parentInsertName"] = parentInsertName,
            ["parentInsertSourceId"] = parentInsertSourceId,
            ["textHeight"] = attribute.TextHeight.ToString(CultureInfo.InvariantCulture),
            ["textStyleName"] = attribute.TextStyleName,
            ["rotation"] = (attribute.Rotation + options.Transform.RotationDegrees).ToString(CultureInfo.InvariantCulture),
            ["isInvisible"] = attribute.IsInvisible.ToString(),
            ["isConstant"] = attribute.IsConstant.ToString(),
            ["fieldLength"] = attribute.FieldLength.ToString(CultureInfo.InvariantCulture)
        };

        return new RawPrimitive(
            bounds,
            transform => new TextPrimitive(value, transform.Rect(bounds))
            {
                SourceId = sourceId,
                Layer = effectiveLayer,
                FontSize = height * options.Transform.LengthScale,
                StrokeWidth = StrokeWidth(attribute) * options.Transform.LengthScale,
                Source = CreateEntitySource(
                    documentSource,
                    attribute,
                    sourceId,
                    options,
                    effectiveLayer,
                    blockName: parentInsertName,
                    properties: properties)
            },
            options.ExpandedFromBlock,
            FromInsertAttribute: true);
    }

    private static IEnumerable<RawPrimitive> CreateDimensionPrimitives(
        DxfDimensionBase dimension,
        string sourceId,
        DocumentSourceInfo documentSource,
        EntityExtractionOptions options,
        string effectiveLayer)
    {
        var line = TryCreateLinearDimensionLine(dimension, options.Transform);
        var label = ResolveDimensionLabel(dimension, documentSource.DefaultDrawingUnits);
        if (line is not null)
        {
            yield return CreateDimensionLine(dimension, sourceId, documentSource, options, effectiveLayer, line.Value);
        }

        if (!string.IsNullOrWhiteSpace(label))
        {
            yield return CreateDimensionText(dimension, sourceId, documentSource, options, effectiveLayer, label!, line);
        }
    }

    private static RawPrimitive CreateDimensionLine(
        DxfDimensionBase dimension,
        string sourceId,
        DocumentSourceInfo documentSource,
        EntityExtractionOptions options,
        string effectiveLayer,
        PlanLineSegment line)
    {
        var dimensionSourceId = $"{sourceId}:dimension:line";
        var properties = DimensionProperties(dimension, "line");

        return new RawPrimitive(
            line.Bounds,
            transform => new LinePrimitive(new PlanLineSegment(transform.Point(line.Start), transform.Point(line.End)))
            {
                SourceId = dimensionSourceId,
                Layer = effectiveLayer,
                StrokeWidth = StrokeWidth(dimension) * options.Transform.LengthScale,
                Source = CreateEntitySource(
                    documentSource,
                    dimension,
                    dimensionSourceId,
                    options,
                    effectiveLayer,
                    blockName: dimension.BlockName,
                    properties: properties)
            },
            options.ExpandedFromBlock,
            FromDimensionEntity: true);
    }

    private static RawPrimitive CreateDimensionText(
        DxfDimensionBase dimension,
        string sourceId,
        DocumentSourceInfo documentSource,
        EntityExtractionOptions options,
        string effectiveLayer,
        string label,
        PlanLineSegment? line)
    {
        var dimensionSourceId = $"{sourceId}:dimension:text";
        var textCenter = DimensionTextPoint(dimension, options.Transform, line);
        var lineLength = line?.Length ?? Math.Max(1, dimension.ActualMeasurement * options.Transform.LengthScale);
        var height = Math.Clamp(lineLength * 0.035, 1, 12);
        var width = Math.Max(height, label.Length * height * 0.6);
        var bounds = new PlanRect(textCenter.X - (width / 2), textCenter.Y - (height / 2), width, height);
        var properties = DimensionProperties(dimension, "text");
        properties["dimensionResolvedText"] = label;

        return new RawPrimitive(
            bounds,
            transform => new TextPrimitive(label, transform.Rect(bounds))
            {
                SourceId = dimensionSourceId,
                Layer = effectiveLayer,
                FontSize = height,
                StrokeWidth = StrokeWidth(dimension) * options.Transform.LengthScale,
                Source = CreateEntitySource(
                    documentSource,
                    dimension,
                    dimensionSourceId,
                    options,
                    effectiveLayer,
                    blockName: dimension.BlockName,
                    properties: properties)
            },
            options.ExpandedFromBlock,
            FromDimensionEntity: true);
    }

    private static RawPrimitive CreateArc(
        DxfArc arc,
        string sourceId,
        DocumentSourceInfo documentSource,
        EntityExtractionOptions options,
        string effectiveLayer)
    {
        var center = options.Transform.Point(RawPoint(arc.Center));
        var sourceProperties = new Dictionary<string, string>
        {
            ["startAngle"] = arc.StartAngle.ToString(CultureInfo.InvariantCulture),
            ["endAngle"] = arc.EndAngle.ToString(CultureInfo.InvariantCulture),
            ["radius"] = arc.Radius.ToString(CultureInfo.InvariantCulture)
        };

        if (!options.Transform.TryGetUniformPositiveScale(out var scale, out var rotationDegrees))
        {
            sourceProperties["approximatedFromNonUniformBlockTransform"] = "true";
            var rawPoints = SampleArcPoints(arc, options.Transform).ToArray();
            var rawBounds = BoundsForPoints(rawPoints);

            return new RawPrimitive(
                rawBounds,
                transform => new PolylinePrimitive(rawPoints.Select(transform.Point).ToArray(), Closed: false)
                {
                    SourceId = sourceId,
                    Layer = effectiveLayer,
                    StrokeWidth = StrokeWidth(arc) * options.Transform.LengthScale,
                    Source = CreateEntitySource(documentSource, arc, sourceId, options, effectiveLayer, properties: sourceProperties)
                },
                options.ExpandedFromBlock);
        }

        var radius = arc.Radius * scale;
        var transformedStartAngle = arc.StartAngle + rotationDegrees;
        var transformedEndAngle = arc.EndAngle + rotationDegrees;
        var bounds = new PlanRect(
            center.X - radius,
            center.Y - radius,
            radius * 2,
            radius * 2);

        return new RawPrimitive(
            bounds,
            transform => new ArcPrimitive(
                transform.Point(center),
                radius,
                DegreesToRadians(-transformedEndAngle),
                DegreesToRadians(transformedEndAngle - transformedStartAngle))
            {
                SourceId = sourceId,
                Layer = effectiveLayer,
                StrokeWidth = StrokeWidth(arc) * options.Transform.LengthScale,
                Source = CreateEntitySource(
                    documentSource,
                    arc,
                    sourceId,
                    options,
                    effectiveLayer,
                    properties: sourceProperties)
            },
            options.ExpandedFromBlock);
    }

    private static RawPrimitive CreateCircle(
        DxfCircle circle,
        string sourceId,
        DocumentSourceInfo documentSource,
        EntityExtractionOptions options,
        string effectiveLayer)
    {
        var center = RawPoint(circle.Center);
        var points = Enumerable.Range(0, 32)
            .Select(index =>
            {
                var angle = (Math.PI * 2 * index) / 32.0;
                return new PlanPoint(
                    center.X + (Math.Cos(angle) * circle.Radius),
                    center.Y + (Math.Sin(angle) * circle.Radius));
            })
            .Select(options.Transform.Point)
            .ToArray();
        var bounds = BoundsForPoints(points);

        return new RawPrimitive(
            bounds,
            transform => new PolylinePrimitive(points.Select(transform.Point).ToArray(), Closed: true)
            {
                SourceId = sourceId,
                Layer = effectiveLayer,
                StrokeWidth = StrokeWidth(circle) * options.Transform.LengthScale,
                Source = CreateEntitySource(
                    documentSource,
                    circle,
                    sourceId,
                    options,
                    effectiveLayer,
                    properties: new Dictionary<string, string>
                    {
                        ["radius"] = circle.Radius.ToString(CultureInfo.InvariantCulture)
                    })
            },
            options.ExpandedFromBlock);
    }

    private static RawPrimitive CreateInsert(
        DxfInsert insert,
        string sourceId,
        DocumentSourceInfo documentSource,
        EntityExtractionOptions options,
        string effectiveLayer)
    {
        var location = options.Transform.Point(RawPoint(insert.Location));
        var size = Math.Max(Math.Max(Math.Abs(insert.XScaleFactor), Math.Abs(insert.YScaleFactor)) * 10, 2);
        var scaledSize = size * options.Transform.LengthScale;
        var bounds = new PlanRect(location.X - (scaledSize / 2), location.Y - (scaledSize / 2), scaledSize, scaledSize);

        return new RawPrimitive(
            bounds,
            transform => new SymbolPrimitive(insert.Name, transform.Rect(bounds))
            {
                SourceId = sourceId,
                Layer = effectiveLayer,
                StrokeWidth = StrokeWidth(insert) * options.Transform.LengthScale,
                Source = CreateEntitySource(
                    documentSource,
                    insert,
                    sourceId,
                    options,
                    effectiveLayer,
                    blockName: insert.Name,
                    properties: new Dictionary<string, string>
                    {
                        ["insertName"] = insert.Name,
                        ["xScaleFactor"] = insert.XScaleFactor.ToString(CultureInfo.InvariantCulture),
                        ["yScaleFactor"] = insert.YScaleFactor.ToString(CultureInfo.InvariantCulture),
                        ["zScaleFactor"] = insert.ZScaleFactor.ToString(CultureInfo.InvariantCulture),
                        ["rotation"] = insert.Rotation.ToString(CultureInfo.InvariantCulture),
                        ["rowCount"] = insert.RowCount.ToString(CultureInfo.InvariantCulture),
                        ["columnCount"] = insert.ColumnCount.ToString(CultureInfo.InvariantCulture),
                        ["hasAttributes"] = insert.HasAttributes.ToString()
                    })
            },
            options.ExpandedFromBlock);
    }

    private static PlanRect BoundsForPoints(IReadOnlyList<PlanPoint> points)
    {
        if (points.Count == 0)
        {
            return PlanRect.Empty;
        }

        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        var right = points.Max(point => point.X);
        var bottom = points.Max(point => point.Y);
        return PlanRect.FromEdges(left, top, right, bottom);
    }

    private static PlanPoint RawPoint(DxfPoint point) => new(point.X, point.Y);

    private static double StrokeWidth(DxfEntity entity) =>
        entity.LineweightEnumValue > 0 ? entity.LineweightEnumValue / 100.0 : 0;

    private static string AttributeText(DxfAttribute attribute) =>
        string.IsNullOrWhiteSpace(attribute.Value)
            ? attribute.MText?.Text?.Trim() ?? string.Empty
            : attribute.Value.Trim();

    private static PlanLineSegment? TryCreateLinearDimensionLine(
        DxfDimensionBase dimension,
        RawEntityTransform transform)
    {
        return dimension switch
        {
            DxfAlignedDimension aligned => NonZeroLine(
                transform.Point(RawPoint(aligned.DefinitionPoint2)),
                transform.Point(RawPoint(aligned.DefinitionPoint3))),
            DxfRotatedDimension rotated => NonZeroLine(
                transform.Point(RawPoint(rotated.DefinitionPoint2)),
                transform.Point(RawPoint(rotated.DefinitionPoint3))),
            _ => null
        };
    }

    private static PlanLineSegment? NonZeroLine(PlanPoint start, PlanPoint end)
    {
        var line = new PlanLineSegment(start, end);
        return line.Length <= 0.001 ? null : line;
    }

    private static PlanPoint DimensionTextPoint(
        DxfDimensionBase dimension,
        RawEntityTransform transform,
        PlanLineSegment? line)
    {
        var textPoint = RawPoint(dimension.TextMidPoint);
        if (Math.Abs(textPoint.X) > 0.001 || Math.Abs(textPoint.Y) > 0.001)
        {
            return transform.Point(textPoint);
        }

        return line?.Midpoint
            ?? transform.Point(RawPoint(dimension.DefinitionPoint1));
    }

    private static string? ResolveDimensionLabel(DxfDimensionBase dimension, DxfUnits defaultDrawingUnits)
    {
        var text = (dimension.Text ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(text) && !string.Equals(text, "<>", StringComparison.Ordinal))
        {
            if (text.Contains("<>", StringComparison.Ordinal)
                && dimension.ActualMeasurement > 0
                && TryUnitSuffix(defaultDrawingUnits, out var replacementSuffix))
            {
                return text.Replace("<>", $"{FormatDimensionNumber(dimension.ActualMeasurement)} {replacementSuffix}", StringComparison.Ordinal);
            }

            return text.Contains("<>", StringComparison.Ordinal) ? null : text;
        }

        if (dimension.ActualMeasurement > 0 && TryUnitSuffix(defaultDrawingUnits, out var suffix))
        {
            return $"{FormatDimensionNumber(dimension.ActualMeasurement)} {suffix}";
        }

        return null;
    }

    private static bool TryUnitSuffix(DxfUnits units, out string suffix)
    {
        suffix = units switch
        {
            DxfUnits.Millimeters => "mm",
            DxfUnits.Centimeters => "cm",
            DxfUnits.Meters => "m",
            DxfUnits.Inches or DxfUnits.USSurveyInch => "in",
            DxfUnits.Feet or DxfUnits.USSurveyFeet => "ft",
            _ => string.Empty
        };

        return suffix.Length > 0;
    }

    private static Dictionary<string, string> DimensionProperties(
        DxfDimensionBase dimension,
        string primitiveKind) =>
        new()
        {
            ["dimensionPrimitiveKind"] = primitiveKind,
            ["dimensionType"] = dimension.DimensionType.ToString(),
            ["dimensionStyleName"] = dimension.DimensionStyleName,
            ["dimensionBlockName"] = dimension.BlockName,
            ["dimensionText"] = dimension.Text ?? string.Empty,
            ["actualMeasurement"] = dimension.ActualMeasurement.ToString(CultureInfo.InvariantCulture),
            ["textRotationAngle"] = dimension.TextRotationAngle.ToString(CultureInfo.InvariantCulture),
            ["horizontalDirectionAngle"] = dimension.HorizontalDirectionAngle.ToString(CultureInfo.InvariantCulture)
        };

    private static string FormatDimensionNumber(double value) =>
        Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static string EffectiveLayer(string? entityLayer, string? inheritedLayer) =>
        string.IsNullOrWhiteSpace(entityLayer) || string.Equals(entityLayer, "0", StringComparison.OrdinalIgnoreCase)
            ? inheritedLayer ?? entityLayer ?? string.Empty
            : entityLayer;

    private static IEnumerable<PlanPoint> SampleArcPoints(DxfArc arc, RawEntityTransform transform)
    {
        const int segmentCount = 16;
        var center = RawPoint(arc.Center);
        var sweep = arc.EndAngle - arc.StartAngle;

        for (var index = 0; index <= segmentCount; index++)
        {
            var angle = DegreesToRadians(arc.StartAngle + (sweep * index / segmentCount));
            yield return transform.Point(
                new PlanPoint(
                    center.X + (Math.Cos(angle) * arc.Radius),
                    center.Y + (Math.Sin(angle) * arc.Radius)));
        }
    }

    private static PrimitiveSourceMetadata CreateEntitySource(
        DocumentSourceInfo documentSource,
        DxfEntity entity,
        string sourceId,
        EntityExtractionOptions options,
        string effectiveLayer,
        string? blockName = null,
        double? lineWeightOverride = null,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        var mergedProperties = new Dictionary<string, string>
        {
            ["entityTypeString"] = entity.EntityTypeString,
            ["isVisible"] = entity.IsVisible.ToString(),
            ["isInPaperSpace"] = entity.IsInPaperSpace.ToString(),
            ["lineTypeScale"] = entity.LineTypeScale.ToString(CultureInfo.InvariantCulture),
            ["lineweightEnumValue"] = entity.LineweightEnumValue.ToString(CultureInfo.InvariantCulture),
            ["color24Bit"] = entity.Color24Bit.ToString(CultureInfo.InvariantCulture),
            ["colorName"] = entity.ColorName ?? string.Empty,
            ["effectiveLayer"] = effectiveLayer
        };

        if (!string.Equals(entity.Layer, effectiveLayer, StringComparison.OrdinalIgnoreCase))
        {
            mergedProperties["blockEntityLayer"] = entity.Layer;
        }

        if (options.BlockName is not null)
        {
            mergedProperties["blockName"] = options.BlockName;
        }

        if (options.ParentInsertSourceId is not null)
        {
            mergedProperties["parentInsertSourceId"] = options.ParentInsertSourceId;
        }

        foreach (var property in options.Properties)
        {
            mergedProperties[property.Key] = property.Value;
        }

        if (properties is not null)
        {
            foreach (var property in properties)
            {
                mergedProperties[property.Key] = property.Value;
            }
        }

        return new PrimitiveSourceMetadata
        {
            SourceFormat = "dxf",
            SourceDocumentId = documentSource.DocumentId,
            SourceName = documentSource.SourceName,
            SourcePath = documentSource.SourcePath,
            SourceId = sourceId,
            EntityType = entity.EntityTypeString,
            Layer = effectiveLayer,
            Color = ResolveColor(entity),
            LineType = entity.LineTypeName,
            LineWeight = lineWeightOverride ?? StrokeWidth(entity),
            DrawingSpace = entity.IsInPaperSpace ? SourceDrawingSpace.Paper : SourceDrawingSpace.Model,
            BlockName = blockName ?? options.BlockName,
            Properties = mergedProperties
        };
    }

    private static string ResolveColor(DxfEntity entity)
    {
        if (!string.IsNullOrWhiteSpace(entity.ColorName))
        {
            return entity.ColorName;
        }

        if (entity.Color24Bit != 0)
        {
            return $"#{entity.Color24Bit:X6}";
        }

        return entity.Color.ToString() ?? string.Empty;
    }

    private static double DegreesToRadians(double degrees) =>
        degrees * Math.PI / 180.0;

    private static string SafeSourceSegment(string value)
    {
        var safe = new string(value.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "block" : safe;
    }

    private sealed record RawPrimitive(
        PlanRect Bounds,
        Func<DxfDrawingTransform, PlanPrimitive> Create,
        bool ExpandedFromBlock = false,
        bool FromInsertAttribute = false,
        bool FromDimensionEntity = false);

    private sealed record EntityExtractionOptions(
        RawEntityTransform Transform,
        string? InheritedLayer,
        string? BlockName,
        string? ParentInsertSourceId,
        bool ExpandedFromBlock,
        IReadOnlyDictionary<string, string> Properties)
    {
        public static EntityExtractionOptions Root { get; } = new(
            RawEntityTransform.Identity,
            null,
            null,
            null,
            false,
            new Dictionary<string, string>());
    }

    private readonly record struct RawEntityTransform(
        double A,
        double B,
        double C,
        double D,
        double Tx,
        double Ty)
    {
        public static RawEntityTransform Identity { get; } = new(1, 0, 0, 1, 0, 0);

        public double LengthScale => Math.Sqrt(Math.Abs((A * D) - (B * C)));

        public double RotationDegrees => Math.Atan2(C, A) * 180.0 / Math.PI;

        public static RawEntityTransform FromInsert(
            DxfInsert insert,
            PlanPoint blockBasePoint,
            int row,
            int column)
        {
            var scaleX = insert.XScaleFactor == 0 ? 1 : insert.XScaleFactor;
            var scaleY = insert.YScaleFactor == 0 ? 1 : insert.YScaleFactor;
            var rotation = DegreesToRadians(insert.Rotation);
            var cos = Math.Cos(rotation);
            var sin = Math.Sin(rotation);
            var a = scaleX * cos;
            var b = -scaleY * sin;
            var c = scaleX * sin;
            var d = scaleY * cos;
            var offsetX = column * insert.ColumnSpacing;
            var offsetY = row * insert.RowSpacing;
            var tx = insert.Location.X + (a * (offsetX - blockBasePoint.X)) + (b * (offsetY - blockBasePoint.Y));
            var ty = insert.Location.Y + (c * (offsetX - blockBasePoint.X)) + (d * (offsetY - blockBasePoint.Y));

            return new RawEntityTransform(a, b, c, d, tx, ty);
        }

        public RawEntityTransform Compose(RawEntityTransform child) =>
            new(
                (A * child.A) + (B * child.C),
                (A * child.B) + (B * child.D),
                (C * child.A) + (D * child.C),
                (C * child.B) + (D * child.D),
                (A * child.Tx) + (B * child.Ty) + Tx,
                (C * child.Tx) + (D * child.Ty) + Ty);

        public PlanPoint Point(PlanPoint point) =>
            new(
                (A * point.X) + (B * point.Y) + Tx,
                (C * point.X) + (D * point.Y) + Ty);

        public PlanRect Rect(PlanRect rect)
        {
            var topLeft = Point(new PlanPoint(rect.Left, rect.Top));
            var topRight = Point(new PlanPoint(rect.Right, rect.Top));
            var bottomRight = Point(new PlanPoint(rect.Right, rect.Bottom));
            var bottomLeft = Point(new PlanPoint(rect.Left, rect.Bottom));

            return BoundsForPoints(new[] { topLeft, topRight, bottomRight, bottomLeft });
        }

        public bool TryGetUniformPositiveScale(out double scale, out double rotationDegrees)
        {
            var scaleX = Math.Sqrt((A * A) + (C * C));
            var scaleY = Math.Sqrt((B * B) + (D * D));
            var dot = (A * B) + (C * D);
            var determinant = (A * D) - (B * C);
            var tolerance = Math.Max(0.001, Math.Max(scaleX, scaleY) * 0.001);

            if (determinant <= 0
                || Math.Abs(scaleX - scaleY) > tolerance
                || Math.Abs(dot) > tolerance)
            {
                scale = 0;
                rotationDegrees = 0;
                return false;
            }

            scale = scaleX;
            rotationDegrees = RotationDegrees;
            return true;
        }
    }

    private sealed record DocumentSourceInfo(
        string DocumentId,
        string? SourceName,
        string? SourcePath,
        DxfUnits DefaultDrawingUnits);

    private sealed class DxfDrawingTransform
    {
        private readonly PlanRect _rawBounds;
        private readonly double _margin;

        public DxfDrawingTransform(PlanRect rawBounds, double margin)
        {
            _rawBounds = rawBounds;
            _margin = margin;
            PageWidth = Math.Max(1, rawBounds.Width) + (margin * 2);
            PageHeight = Math.Max(1, rawBounds.Height) + (margin * 2);
        }

        public double PageWidth { get; }

        public double PageHeight { get; }

        public PlanPoint Point(PlanPoint raw) =>
            new(
                raw.X - _rawBounds.Left + _margin,
                _rawBounds.Bottom - raw.Y + _margin);

        public PlanRect Rect(PlanRect raw)
        {
            var topLeft = Point(new PlanPoint(raw.Left, raw.Top));
            var topRight = Point(new PlanPoint(raw.Right, raw.Top));
            var bottomRight = Point(new PlanPoint(raw.Right, raw.Bottom));
            var bottomLeft = Point(new PlanPoint(raw.Left, raw.Bottom));

            return BoundsForPoints(new[] { topLeft, topRight, bottomRight, bottomLeft });
        }
    }
}
