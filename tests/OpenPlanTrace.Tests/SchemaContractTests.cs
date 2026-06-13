using System.Reflection;
using System.Text;
using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class SchemaContractTests
{
    [Fact]
    public async Task EmbeddedScanSchema_MatchesDocumentedSchemaArtifact()
    {
        var schema = PlanTraceJsonSchema.ReadCurrent();
        using var document = JsonDocument.Parse(schema);

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("urn:openplantrace:schema:scan:v44", document.RootElement.GetProperty("$id").GetString());
        Assert.Equal(
            PlanTraceExport.CurrentSchemaVersion,
            document.RootElement.GetProperty("x-openplantrace-schemaVersion").GetString());

        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "schemas",
            $"{PlanTraceExport.CurrentSchemaVersion}.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing documented schema artifact: {schemaPath}");
        Assert.Equal(Normalize(File.ReadAllText(schemaPath)), Normalize(schema));

        await using var stream = new MemoryStream();
        await PlanTraceJsonSchema.WriteCurrentAsync(stream);
        Assert.Equal(schema, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public async Task ScanSchema_TopLevelContractMatchesJsonExporter()
    {
        using var schemaDocument = JsonDocument.Parse(PlanTraceJsonSchema.ReadCurrent());
        var requiredProperties = ReadStringArray(schemaDocument.RootElement.GetProperty("required"));
        var schemaProperties = schemaDocument.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var exportProperties = typeof(PlanTraceExport)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => ToCamelCase(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(exportProperties, requiredProperties.Order(StringComparer.Ordinal).ToArray());
        foreach (var exportProperty in exportProperties)
        {
            Assert.Contains(exportProperty, schemaProperties);
        }

        var result = await CreateScanResultAsync();
        using var exportedDocument = JsonDocument.Parse(
            PlanTraceJsonExporter.Serialize(
                result,
                new PlanTraceJsonExportOptions { WriteIndented = false }));

        Assert.Equal(
            PlanTraceExport.CurrentSchemaVersion,
            exportedDocument.RootElement.GetProperty("schemaVersion").GetString());
        foreach (var requiredProperty in requiredProperties)
        {
            Assert.True(
                exportedDocument.RootElement.TryGetProperty(requiredProperty, out _),
                $"Export JSON is missing schema-required top-level property '{requiredProperty}'.");
        }
    }

    [Fact]
    public async Task EmbeddedVisualSnapshotSchema_MatchesDocumentedSchemaArtifact()
    {
        var schema = PlanOverlaySnapshotJsonSchema.ReadCurrent();
        using var document = JsonDocument.Parse(schema);

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("urn:openplantrace:schema:visual-snapshot:v2", document.RootElement.GetProperty("$id").GetString());
        Assert.Equal(
            PlanOverlaySnapshot.CurrentSchemaVersion,
            document.RootElement.GetProperty("x-openplantrace-schemaVersion").GetString());

        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "schemas",
            $"{PlanOverlaySnapshot.CurrentSchemaVersion}.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing documented schema artifact: {schemaPath}");
        Assert.Equal(Normalize(File.ReadAllText(schemaPath)), Normalize(schema));

        await using var stream = new MemoryStream();
        await PlanOverlaySnapshotJsonSchema.WriteCurrentAsync(stream);
        Assert.Equal(schema, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public async Task VisualSnapshotSchema_TopLevelContractMatchesExporter()
    {
        using var schemaDocument = JsonDocument.Parse(PlanOverlaySnapshotJsonSchema.ReadCurrent());
        var requiredProperties = ReadStringArray(schemaDocument.RootElement.GetProperty("required"));
        var schemaProperties = schemaDocument.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var snapshotProperties = typeof(PlanOverlaySnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => ToCamelCase(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(snapshotProperties, requiredProperties.Order(StringComparer.Ordinal).ToArray());
        foreach (var snapshotProperty in snapshotProperties)
        {
            Assert.Contains(snapshotProperty, schemaProperties);
        }

        var result = await CreateScanResultAsync();
        using var exportedDocument = JsonDocument.Parse(
            PlanOverlaySnapshotJsonExporter.Serialize(
                result,
                new PlanOverlaySnapshotJsonExportOptions { WriteIndented = false },
                new Dictionary<int, string> { [1] = "overlays/page-1.svg" }));

        Assert.Equal(
            PlanOverlaySnapshot.CurrentSchemaVersion,
            exportedDocument.RootElement.GetProperty("schemaVersion").GetString());
        foreach (var requiredProperty in requiredProperties)
        {
            Assert.True(
                exportedDocument.RootElement.TryGetProperty(requiredProperty, out _),
                $"Visual snapshot JSON is missing schema-required top-level property '{requiredProperty}'.");
        }

        var page = exportedDocument.RootElement.GetProperty("pages")[0];
        Assert.Equal(1, page.GetProperty("pageNumber").GetInt32());
        Assert.Equal("overlays/page-1.svg", page.GetProperty("svgPath").GetString());
        Assert.True(page.GetProperty("layers").GetArrayLength() > 0);
    }

    [Fact]
    public async Task EmbeddedPlacementSchema_MatchesDocumentedSchemaArtifact()
    {
        var schema = PlanPlacementJsonSchema.ReadCurrent();
        using var document = JsonDocument.Parse(schema);

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("urn:openplantrace:schema:placement:v1", document.RootElement.GetProperty("$id").GetString());
        Assert.Equal(
            PlanPlacementExport.CurrentSchemaVersion,
            document.RootElement.GetProperty("x-openplantrace-schemaVersion").GetString());

        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "schemas",
            $"{PlanPlacementExport.CurrentSchemaVersion}.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing documented schema artifact: {schemaPath}");
        Assert.Equal(Normalize(File.ReadAllText(schemaPath)), Normalize(schema));

        await using var stream = new MemoryStream();
        await PlanPlacementJsonSchema.WriteCurrentAsync(stream);
        Assert.Equal(schema, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public async Task PlacementSchema_TopLevelContractMatchesExporter()
    {
        using var schemaDocument = JsonDocument.Parse(PlanPlacementJsonSchema.ReadCurrent());
        var requiredProperties = ReadStringArray(schemaDocument.RootElement.GetProperty("required"));
        var schemaProperties = schemaDocument.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var placementProperties = typeof(PlanPlacementExport)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => ToCamelCase(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(placementProperties, requiredProperties.Order(StringComparer.Ordinal).ToArray());
        foreach (var placementProperty in placementProperties)
        {
            Assert.Contains(placementProperty, schemaProperties);
        }

        var result = await CreateScanResultAsync();
        using var exportedDocument = JsonDocument.Parse(
            PlanPlacementJsonExporter.Serialize(
                result,
                new PlanPlacementJsonExportOptions { WriteIndented = false }));

        Assert.Equal(
            PlanPlacementExport.CurrentSchemaVersion,
            exportedDocument.RootElement.GetProperty("schemaVersion").GetString());
        foreach (var requiredProperty in requiredProperties)
        {
            Assert.True(
                exportedDocument.RootElement.TryGetProperty(requiredProperty, out _),
                $"Placement JSON is missing schema-required top-level property '{requiredProperty}'.");
        }

        var coordinateSystem = exportedDocument.RootElement.GetProperty("coordinateSystem");
        Assert.Equal("OpenPlanTracePageCoordinates", coordinateSystem.GetProperty("coordinateSpace").GetString());
        Assert.Equal("x,y", coordinateSystem.GetProperty("coordinateOrder").GetString());
        Assert.True(exportedDocument.RootElement.GetProperty("walls").GetArrayLength() > 0);
    }

    [Fact]
    public void VisualSnapshotSchema_DefinesPageLayerBoundsAndIssueShapes()
    {
        using var schemaDocument = JsonDocument.Parse(PlanOverlaySnapshotJsonSchema.ReadCurrent());

        AssertDefinitionRequires(schemaDocument, "pageSnapshot", "pageNumber", "width", "height", "pageBounds", "detectionBounds", "detectionCoverage", "drawableItemCount", "primitiveCount", "svgPath", "layers", "reviewQueueCount", "reviewQueueKindBreakdown", "reviewQueueSeverityBreakdown", "reviewQueue", "issues");
        AssertDefinitionRequires(schemaDocument, "layerSnapshot", "name", "count", "bounds", "averageConfidence", "minimumConfidence", "maximumConfidence", "breakdown");
        AssertDefinitionRequires(schemaDocument, "reviewQueueItem", "id", "kind", "detector", "itemId", "priority", "severity", "pageNumber", "bounds", "confidence", "recommendedAction", "sourcePrimitiveCount", "sourceLayerCount", "evidence");
        AssertDefinitionRequires(schemaDocument, "snapshotIssue", "code", "severity", "message", "pageNumber");
        AssertDefinitionRequires(schemaDocument, "rect", "x", "y", "width", "height", "left", "top", "right", "bottom", "centerX", "centerY", "area");
    }

    [Fact]
    public void PlacementSchema_DefinesDownstreamPlacementShapes()
    {
        using var schemaDocument = JsonDocument.Parse(PlanPlacementJsonSchema.ReadCurrent());

        AssertDefinitionRequires(schemaDocument, "document", "id", "sourceName", "sourcePath", "sourceFormat", "loader", "sourceKind", "effectiveSourceKind", "clipboardContentKind", "fileExtension", "contentType", "isDwgDerived", "dwgConversion", "dwgConverter", "dwgIntermediateFormat", "dwgIntermediateLoader", "rasterAdapter", "rasterExtractor", "rasterExtractorVersion", "rasterModelName", "rasterModelVersion", "properties");
        AssertDefinitionRequires(schemaDocument, "summary", "pageCount", "mainFloorplanRegionCount", "surfacePatternCount", "wallCount", "structuralWallCount", "excludedWallCount", "roomCount", "openingCount", "anchoredOpeningCount", "unanchoredOpeningCount", "objectAggregateCount", "wallGraphRepairCandidateCount", "suppressedChildObjectCount", "routingBarrierCount", "routingPassageCount", "routingObstacleCount", "routingRoomUseHintCount", "routingSuppressedObjectCount", "routingItemCount", "totalPlacementEntityCount", "reliabilityTrackedEntityCount", "coordinateReadyEntityCount", "metricReadyEntityCount", "reviewRequiredEntityCount", "coordinateReadyRatio", "metricReadyRatio", "issueCount", "infoIssueCount", "warningIssueCount", "errorIssueCount", "sourcePrimitiveReferenceCount", "uniqueSourcePrimitiveReferenceCount", "importReadiness", "pageSummaries", "evidence");
        AssertDefinitionRequires(schemaDocument, "importReadiness", "grade", "score", "readyForGeometryImport", "readyForMetricImport", "readyForRoutingImport", "requiresReview", "blockingIssueCodes", "reviewIssueCodes", "recommendedActions", "evidence");
        AssertDefinitionRequires(schemaDocument, "pageSummary", "pageNumber", "pageBounds", "mainFloorplanBounds", "detectionBounds", "detectionBoundsMillimeters", "surfacePatternCount", "wallCount", "structuralWallCount", "excludedWallCount", "roomCount", "openingCount", "anchoredOpeningCount", "unanchoredOpeningCount", "objectAggregateCount", "wallGraphRepairCandidateCount", "routingItemCount", "reliabilityTrackedEntityCount", "coordinateReadyEntityCount", "metricReadyEntityCount", "reviewRequiredEntityCount", "issueCount");
        AssertDefinitionRequires(schemaDocument, "coordinateSystem", "coordinateSpace", "unit", "origin", "xAxisDirection", "yAxisDirection", "geometryBasis", "coordinateOrder", "boundsKind", "precision", "realWorldUnit", "millimetersPerDrawingUnit", "note", "pageFrames");
        AssertDefinitionRequires(schemaDocument, "pageCoordinateFrame", "pageNumber", "width", "height", "bounds", "pageToNormalizedTransform", "normalizedToPageTransform");
        AssertDefinitionRequires(schemaDocument, "calibration", "drawingUnit", "realWorldUnit", "millimetersPerDrawingUnit", "hasReliableMeasurementScale", "metricCoordinateStatus", "measurementCheckedCount", "measurementOutlierCount", "scaleGroups", "evidence");
        AssertDefinitionRequires(schemaDocument, "qualityGate", "coordinateTrust", "metricTrust", "readyForCoordinatePlacement", "readyForMetricPlacement", "qualityGrade", "qualityConfidence", "requiresReview", "hasReliableCalibration", "evidence");
        AssertDefinitionRequires(schemaDocument, "surfacePattern", "id", "pageNumber", "kind", "orientation", "bounds", "boundsMillimeters", "center", "centerMillimeters", "millimetersPerDrawingUnit", "sourceRegionId", "lineCount", "horizontalLineCount", "verticalLineCount", "intersectionCount", "horizontalMedianSpacing", "verticalMedianSpacing", "medianSpacing", "excludedFromWallDetection", "excludedFromStructuralTopology", "confidence", "requiresReview", "recommendedAction", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "wall", "id", "pageNumber", "centerLine", "bounds", "drawingLength", "thicknessDrawingUnits", "confidence", "reliability", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "room", "id", "pageNumber", "bounds", "center", "boundary", "wallIds", "drawingArea", "confidence", "reliability", "evidence");
        AssertDefinitionRequires(schemaDocument, "opening", "id", "pageNumber", "type", "operation", "orientation", "centerLine", "bounds", "drawingWidth", "placementStatus", "placement", "hostWallIds", "connectedRoomIds", "confidence", "reliability", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "openingPlacement", "hostWallId", "anchorWallIds", "referenceLine", "startPoint", "endPoint", "startOffsetDrawingUnits", "endOffsetDrawingUnits", "centerOffsetDrawingUnits", "lengthDrawingUnits", "startOffsetMillimeters", "endOffsetMillimeters", "centerOffsetMillimeters", "lengthMillimeters", "hostWallStartParameter", "hostWallEndParameter", "hostWallCenterParameter", "alongVector", "normalVector", "crossWallOffsetDrawingUnits", "confidence", "evidence");
        AssertDefinitionRequires(schemaDocument, "objectAggregate", "id", "pageNumber", "bounds", "boundsMillimeters", "center", "centerMillimeters", "millimetersPerDrawingUnit", "category", "kind", "routingInfluence", "structuralInfluence", "suppressChildObjectsForRouting", "childObjectCount", "childObjectIds", "composition", "requiresReview", "confidence", "reliability", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "objectAggregateComposition", "categoryCounts", "kindCounts", "sourceKindCounts", "sourceWallComponentKindCounts", "sourceWallComponentIds", "children");
        AssertDefinitionRequires(schemaDocument, "objectAggregateCompositionCount", "value", "count");
        AssertDefinitionRequires(schemaDocument, "objectAggregateChildObject", "objectId", "bounds", "boundsMillimeters", "center", "centerMillimeters", "category", "kind", "sourceKind", "sourceWallComponentId", "sourceWallComponentKind", "label", "symbolName", "detectedTag", "confidence", "sourcePrimitiveIds");
        AssertDefinitionRequires(schemaDocument, "wallGraphRepairCandidate", "id", "pageNumber", "kind", "suggestedAction", "sourceNodeId", "sourcePoint", "sourcePointMillimeters", "targetPoint", "targetPointMillimeters", "targetNodeId", "hostWallId", "gapDistanceDrawingUnits", "gapDistanceMillimeters", "repairLine", "repairLineMillimeters", "bounds", "boundsMillimeters", "wallIds", "sourcePrimitiveIds", "sourceLayers", "confidence", "requiresReview", "recommendedAction", "evidence");
        AssertDefinitionRequires(schemaDocument, "routingLayer", "barriers", "passages", "obstacles", "roomUseHints", "suppressedObjects", "ignoredObjects", "suppressedObjectCandidateIds", "ignoredObjectCandidateIds", "evidence");
        AssertDefinitionRequires(schemaDocument, "routingBarrier", "id", "pageNumber", "sourceId", "sourceKind", "centerLine", "centerLineMillimeters", "bounds", "boundsMillimeters", "thickness", "drawingLength", "lengthMeters", "thicknessMillimeters", "measurementScaleGroupId", "millimetersPerDrawingUnit", "wallComponentId", "wallComponentKind", "excludedFromStructuralTopology", "confidence", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "routingPassage", "id", "pageNumber", "sourceId", "sourceKind", "type", "operation", "orientation", "centerLine", "centerLineMillimeters", "bounds", "boundsMillimeters", "drawingWidth", "widthMillimeters", "measurementScaleGroupId", "millimetersPerDrawingUnit", "hostWallIds", "connectedRoomIds", "connectedRoomLabels", "placement", "confidence", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "routingObstacle", "id", "pageNumber", "sourceId", "sourceKind", "obstacleKind", "routingInfluence", "structuralInfluence", "category", "objectKind", "bounds", "boundsMillimeters", "center", "centerMillimeters", "millimetersPerDrawingUnit", "label", "roomId", "roomLabel", "suppressesChildObjects", "childObjectIds", "confidence", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "routingRoomUseHint", "id", "pageNumber", "sourceId", "sourceKind", "roomUseKind", "bounds", "boundsMillimeters", "center", "centerMillimeters", "millimetersPerDrawingUnit", "roomId", "roomLabel", "confidence", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "routingSuppressedObject", "id", "pageNumber", "objectCandidateId", "suppressedByAggregateId", "reason", "action", "replacementRoutingObstacleId", "roomUseHintId", "aggregateRoutingInfluence", "aggregateStructuralInfluence", "candidateCategory", "candidateKind", "candidateBounds", "candidateBoundsMillimeters", "candidateCenter", "candidateCenterMillimeters", "millimetersPerDrawingUnit", "candidateLabel", "roomId", "roomLabel", "confidence", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "routingIgnoredObject", "id", "pageNumber", "objectCandidateId", "reason", "routingInfluence", "structuralInfluence", "candidateCategory", "candidateKind", "candidateSourceKind", "sourceWallComponentId", "sourceWallComponentKind", "candidateBounds", "candidateBoundsMillimeters", "candidateCenter", "candidateCenterMillimeters", "millimetersPerDrawingUnit", "candidateLabel", "roomId", "roomLabel", "suppressedObjectId", "suppressedByAggregateId", "roomUseHintId", "confidence", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "issue", "code", "severity", "message", "pageNumber", "pageNumbers", "itemId", "bounds", "boundsMillimeters", "confidence", "recommendedAction", "sourcePrimitiveIds", "sourceLayers", "evidence", "properties");
        AssertDefinitionRequires(schemaDocument, "reliability", "readyForCoordinatePlacement", "readyForMetricPlacement", "requiresReview", "confidence", "reasons");
        AssertDefinitionRequires(schemaDocument, "line", "start", "end");
        AssertDefinitionRequires(schemaDocument, "point", "x", "y");
        AssertDefinitionRequires(schemaDocument, "vector", "x", "y");

        var routingLayerProperty = schemaDocument.RootElement
            .GetProperty("properties")
            .GetProperty("routingLayer");
        Assert.Equal("#/$defs/routingLayer", routingLayerProperty.GetProperty("$ref").GetString());

        var pageFrameItems = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("coordinateSystem")
            .GetProperty("properties")
            .GetProperty("pageFrames")
            .GetProperty("items");
        Assert.Equal("#/$defs/pageCoordinateFrame", pageFrameItems.GetProperty("$ref").GetString());

        var transformDefinition = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("affineTransform");
        Assert.Equal(6, transformDefinition.GetProperty("minItems").GetInt32());
        Assert.Equal(6, transformDefinition.GetProperty("maxItems").GetInt32());

        var openingPlacementProperty = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("opening")
            .GetProperty("properties")
            .GetProperty("placement");
        Assert.Contains(
            openingPlacementProperty.GetProperty("oneOf").EnumerateArray(),
            item => item.TryGetProperty("$ref", out var reference)
                && reference.GetString() == "#/$defs/openingPlacement");

        var routingPassagePlacementProperty = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("routingPassage")
            .GetProperty("properties")
            .GetProperty("placement");
        Assert.Contains(
            routingPassagePlacementProperty.GetProperty("oneOf").EnumerateArray(),
            item => item.TryGetProperty("$ref", out var reference)
                && reference.GetString() == "#/$defs/openingPlacement");
    }

    [Fact]
    public void ScanSchema_DefinesEvidenceBearingDetectorShapes()
    {
        using var schemaDocument = JsonDocument.Parse(PlanTraceJsonSchema.ReadCurrent());

        AssertDefinitionRequires(schemaDocument, "coordinateSystem", "coordinateSpace", "unit", "origin", "xAxisDirection", "yAxisDirection", "geometryBasis", "coordinateOrder", "boundsKind", "precision", "realWorldUnit", "millimetersPerDrawingUnit", "note", "pageFrames");
        AssertDefinitionRequires(schemaDocument, "pageCoordinateFrame", "pageNumber", "width", "height", "bounds", "pageToNormalizedTransform", "normalizedToPageTransform");
        AssertDefinitionRequires(schemaDocument, "visualAiClassification", "label", "category", "confidence", "modelName", "modelVersion", "inferenceEngine", "pageNumber", "cropBounds", "cropSourceId", "alternatives", "evidence");
        AssertDefinitionRequires(schemaDocument, "visualAiAlternative", "label", "category", "confidence", "evidence");
        AssertDefinitionRequires(schemaDocument, "wall", "id", "centerLine", "bounds", "detectionKind", "wallComponentId", "wallComponentKind", "excludedFromStructuralTopology", "lengthMeters", "thicknessMillimeters", "measurementScaleGroupId", "confidence", "sourcePrimitiveIds", "sourceLayers", "pairEvidence", "evidence");
        AssertDefinitionRequires(schemaDocument, "surfacePattern", "id", "pageNumber", "kind", "orientation", "bounds", "sourceRegionId", "lineCount", "horizontalLineCount", "verticalLineCount", "intersectionCount", "horizontalMedianSpacing", "verticalMedianSpacing", "medianSpacing", "excludedFromWallDetection", "excludedFromStructuralTopology", "confidence", "requiresReview", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "wallPairEvidence", "firstFaceLine", "secondFaceLine", "faceSeparation", "overlapRatio", "score", "firstFaceFragmentCount", "secondFaceFragmentCount", "firstFaceSourcePrimitiveIds", "secondFaceSourcePrimitiveIds");
        AssertDefinitionRequires(schemaDocument, "wallGraphComponent", "id", "pageNumber", "kind", "bounds", "wallIds", "nodeIds", "edgeIds", "sourcePrimitiveIds", "sourceLayers", "wallCount", "nodeCount", "edgeCount", "drawingLength", "confidence", "excludedFromStructuralTopology", "evidence");
        AssertDefinitionRequires(schemaDocument, "wallGraphRepairCandidate", "id", "pageNumber", "kind", "suggestedAction", "sourceNodeId", "sourcePoint", "targetPoint", "targetNodeId", "hostWallId", "gapDistance", "repairLine", "bounds", "wallIds", "sourcePrimitiveIds", "sourceLayers", "confidence", "requiresReview", "evidence");
        AssertDefinitionRequires(schemaDocument, "opening", "id", "type", "operation", "centerLine", "hostWallIds", "connectedRoomIds", "connectedRoomLabels", "connectedRoomLinks", "roomAdjacencyIds", "drawingWidth", "widthMillimeters", "measurementScaleGroupId", "placement", "confidence", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "openingPlacement", "hostWallId", "anchorWallIds", "referenceLine", "startPoint", "endPoint", "startOffsetDrawingUnits", "endOffsetDrawingUnits", "centerOffsetDrawingUnits", "lengthDrawingUnits", "startOffsetMillimeters", "endOffsetMillimeters", "centerOffsetMillimeters", "lengthMillimeters", "hostWallStartParameter", "hostWallEndParameter", "hostWallCenterParameter", "alongVector", "normalVector", "crossWallOffsetDrawingUnits", "confidence", "evidence");
        AssertDefinitionRequires(schemaDocument, "openingRoomConnection", "roomId", "roomLabel", "roomUseKind", "roomAdjacencyIds", "distanceToOpening", "sharesHostWall", "confidence", "evidence");
        AssertDefinitionRequires(schemaDocument, "roomAdjacencyEdge", "id", "kind", "directionFromFirstToSecond", "directionFromSecondToFirst", "sharedBoundaryLength", "sharedWallIds", "openingIds", "evidence");
        AssertDefinitionRequires(schemaDocument, "room", "id", "bounds", "boundary", "wallIds", "drawingArea", "areaSquareMeters", "measurementScaleGroupId", "useKind", "labelSourcePrimitiveIds", "evidence");
        AssertDefinitionRequires(schemaDocument, "objectCandidate", "id", "kind", "category", "sourceKind", "sourceWallComponentId", "sourceWallComponentKind", "bounds", "detectedTag", "detectedTagSourcePrimitiveId", "roomId", "sourcePrimitiveIds", "sourceLayers", "nearbyText", "visualAi", "evidence");
        AssertDefinitionRequires(schemaDocument, "objectGroup", "id", "signature", "count", "candidateIds", "detectedTags", "requiresReview", "confidence", "sourcePrimitiveIds", "sourceLayers", "nearbyText", "visualAi", "evidence");
        AssertDefinitionRequires(schemaDocument, "objectAggregate", "id", "pageNumber", "bounds", "category", "kind", "childObjectCount", "childObjectIds", "objectGroupIds", "composition", "routingInfluence", "structuralInfluence", "suppressChildObjectsForRouting", "roomUseEvidence", "confidence", "sourcePrimitiveIds", "sourceLayers", "requiresReview", "nearbyText", "evidence");
        AssertDefinitionRequires(schemaDocument, "objectAggregateComposition", "categoryCounts", "kindCounts", "sourceKindCounts", "sourceWallComponentKindCounts", "sourceWallComponentIds", "children");
        AssertDefinitionRequires(schemaDocument, "objectAggregateCompositionCount", "value", "count");
        AssertDefinitionRequires(schemaDocument, "objectAggregateChildObject", "objectId", "bounds", "category", "kind", "sourceKind", "sourceWallComponentId", "sourceWallComponentKind", "label", "symbolName", "detectedTag", "confidence", "sourcePrimitiveIds");
        AssertDefinitionRequires(schemaDocument, "routingLayer", "barriers", "passages", "obstacles", "roomUseHints", "suppressedObjects", "ignoredObjects", "suppressedObjectCandidateIds", "ignoredObjectCandidateIds", "evidence");
        AssertDefinitionRequires(schemaDocument, "routingBarrier", "id", "pageNumber", "sourceId", "sourceKind", "centerLine", "bounds", "thickness", "drawingLength", "wallComponentId", "wallComponentKind", "excludedFromStructuralTopology", "confidence", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "routingPassage", "id", "pageNumber", "sourceId", "sourceKind", "type", "operation", "centerLine", "bounds", "drawingWidth", "widthMillimeters", "hostWallIds", "connectedRoomIds", "connectedRoomLabels", "placement", "confidence", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "routingObstacle", "id", "pageNumber", "sourceId", "sourceKind", "obstacleKind", "routingInfluence", "structuralInfluence", "category", "objectKind", "bounds", "suppressesChildObjects", "childObjectIds", "confidence", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "routingRoomUseHint", "id", "pageNumber", "sourceId", "sourceKind", "roomUseKind", "bounds", "roomId", "roomLabel", "confidence", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "routingSuppressedObject", "id", "pageNumber", "objectCandidateId", "suppressedByAggregateId", "reason", "action", "replacementRoutingObstacleId", "roomUseHintId", "aggregateRoutingInfluence", "aggregateStructuralInfluence", "candidateCategory", "candidateKind", "candidateBounds", "confidence", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "routingIgnoredObject", "id", "pageNumber", "objectCandidateId", "reason", "routingInfluence", "structuralInfluence", "candidateCategory", "candidateKind", "candidateSourceKind", "sourceWallComponentId", "sourceWallComponentKind", "candidateBounds", "candidateLabel", "roomId", "roomLabel", "suppressedObjectId", "suppressedByAggregateId", "roomUseHintId", "confidence", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "objectNearbyText", "text", "pageNumber", "bounds", "sourcePrimitiveId", "distance");
        AssertDefinitionRequires(schemaDocument, "roomCluster", "id", "pageNumber", "roomIds", "roomLabels", "kind", "bounds", "drawingArea", "roomAdjacencyIds", "openingIds", "confidence", "evidence");
        AssertDefinitionRequires(schemaDocument, "gridBaySpacing", "id", "axisOrientation", "line", "drawingDistance", "distanceMeters", "measurementScaleGroupId", "confidence", "sourcePrimitiveIds", "sourceLayers", "evidence");
        AssertDefinitionRequires(schemaDocument, "calibration", "drawingUnit", "realWorldUnit", "scaleRatio", "millimetersPerDrawingUnit", "scaleGroups", "evidence");
        AssertDefinitionRequires(schemaDocument, "calibrationScaleGroup", "id", "pageNumber", "scope", "drawingUnit", "evidenceUnit", "millimetersPerDrawingUnit", "evidenceCount", "sourcePrimitiveIds", "sourceRegionIds", "bounds", "evidence");
        AssertDefinitionRequires(schemaDocument, "measurementConsistency", "hasReliableCalibration", "selectedMillimetersPerDrawingUnit", "medianDimensionMillimetersPerDrawingUnit", "dimensionScaleSpreadRatio", "confidence", "checkedCount", "consistentCount", "outlierCount", "outlierRatio", "hasBlockingOutliers", "hasTolerableOutliers", "nonBlockingOutlierCountMaximum", "nonBlockingOutlierRatioMaximum", "blockingScaleSpreadRatioThreshold", "metricImportImpact", "checks");
        AssertDefinitionRequires(schemaDocument, "layerSummary", "name", "likelyCategory", "confidence", "categoryScores", "evidence", "pageNumbers");
        AssertDefinitionRequires(schemaDocument, "layerCategoryScore", "category", "score", "evidence");
        AssertDefinitionRequires(schemaDocument, "quality", "overallConfidence", "grade", "requiresReview", "detectors", "issues", "evidence");
        AssertDefinitionRequires(schemaDocument, "importReadiness", "grade", "score", "readyForGeometryImport", "readyForMetricImport", "readyForRoutingImport", "requiresReview", "blockingIssueCodes", "reviewIssueCodes", "recommendedActions", "evidence");
        AssertDefinitionRequires(schemaDocument, "reviewQueueItem", "id", "kind", "detector", "itemId", "priority", "severity", "pageNumber", "pageNumbers", "bounds", "confidence", "recommendedAction", "sourcePrimitiveIds", "sourceLayers", "evidence", "properties");
        AssertDefinitionRequires(schemaDocument, "detectorQuality", "name", "itemCount", "averageConfidence", "reviewRequiredCount", "evidenceBearingCount", "confidence", "evidence");
        AssertDefinitionRequires(schemaDocument, "qualityIssue", "code", "severity", "message", "confidence", "properties");
        AssertDefinitionRequires(schemaDocument, "diagnostic", "code", "severity", "stage", "scope", "message", "pageNumber", "region", "confidence", "sourcePrimitiveIds", "properties");
        AssertDefinitionRequires(schemaDocument, "annotationItem", "id", "pageNumber", "kind", "text", "marker", "bounds", "confidence", "sourcePrimitiveIds", "sourceLayers", "references", "evidence");
        AssertDefinitionRequires(schemaDocument, "annotationReference", "id", "marker", "text", "bounds", "confidence", "sourcePrimitiveIds", "sourceLayers", "evidence");

        var roomClusterKindEnum = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("roomCluster")
            .GetProperty("properties")
            .GetProperty("kind")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var kind in Enum.GetNames<RoomClusterKind>())
        {
            Assert.Contains(kind, roomClusterKindEnum);
        }

        var roomUseKindEnum = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("room")
            .GetProperty("properties")
            .GetProperty("useKind")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var kind in Enum.GetNames<RoomUseKind>())
        {
            Assert.Contains(kind, roomUseKindEnum);
        }

        var objectCandidateSourceKindEnum = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("objectCandidate")
            .GetProperty("properties")
            .GetProperty("sourceKind")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var kind in Enum.GetNames<ObjectCandidateSourceKind>())
        {
            Assert.Contains(kind, objectCandidateSourceKindEnum);
        }

        var annotationKindEnum = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("annotationBlock")
            .GetProperty("properties")
            .GetProperty("kind")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var kind in Enum.GetNames<PlanAnnotationKind>())
        {
            Assert.Contains(kind, annotationKindEnum);
        }

        var annotationItemKindEnum = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("annotationItem")
            .GetProperty("properties")
            .GetProperty("kind")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var kind in Enum.GetNames<PlanAnnotationItemKind>())
        {
            Assert.Contains(kind, annotationItemKindEnum);
        }
    }

    [Fact]
    public void ViewerRoutingReviewSamples_ExerciseAggregateSuppressionContract()
    {
        var root = FindRepositoryRoot();
        var scanPath = Path.Combine(
            root,
            "tools",
            "OpenPlanTrace.Viewer",
            "wwwroot",
            "samples",
            "routing-review-scan.json");
        using var scanDocument = JsonDocument.Parse(File.ReadAllText(scanPath));
        var scan = scanDocument.RootElement;

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, scan.GetProperty("schemaVersion").GetString());
        var aggregate = Assert.Single(scan.GetProperty("objectAggregates").EnumerateArray());
        Assert.Equal("aggregate-car-1", aggregate.GetProperty("id").GetString());
        Assert.True(aggregate.GetProperty("suppressChildObjectsForRouting").GetBoolean());
        Assert.Equal("RoomUseEvidenceOnly", aggregate.GetProperty("routingInfluence").GetString());
        var composition = aggregate.GetProperty("composition");
        Assert.Equal(5, composition.GetProperty("children").GetArrayLength());
        Assert.Contains(
            composition.GetProperty("sourceKindCounts").EnumerateArray(),
            item => item.GetProperty("value").GetString() == "CadSymbol"
                && item.GetProperty("count").GetInt32() == 5);

        var routing = scan.GetProperty("routingLayer");
        Assert.Equal(5, routing.GetProperty("suppressedObjects").GetArrayLength());
        Assert.Equal(5, routing.GetProperty("suppressedObjectCandidateIds").GetArrayLength());
        Assert.Equal(5, routing.GetProperty("ignoredObjects").GetArrayLength());
        Assert.Equal(5, routing.GetProperty("ignoredObjectCandidateIds").GetArrayLength());
        Assert.DoesNotContain(
            routing.GetProperty("obstacles").EnumerateArray(),
            item => item.GetProperty("sourceId").GetString() == "aggregate-car-1");
        Assert.Contains(
            routing.GetProperty("suppressedObjects").EnumerateArray(),
            item => item.GetProperty("objectCandidateId").GetString() == "car-body"
                && item.GetProperty("suppressedByAggregateId").GetString() == "aggregate-car-1"
                && item.GetProperty("reason").GetString() == "AggregateRoomUseEvidenceOnly"
                && item.GetProperty("action").GetString() == "UseAggregateRoomUseHint"
                && item.GetProperty("roomUseHintId").GetString() == "routing-room-use:aggregate-car-1");
        Assert.Contains(
            routing.GetProperty("ignoredObjects").EnumerateArray(),
            item => item.GetProperty("objectCandidateId").GetString() == "car-body"
                && item.GetProperty("reason").GetString() == "SuppressedByAggregate"
                && item.GetProperty("suppressedObjectId").GetString() == "routing-suppression:car-body");

        var benchmarkPath = Path.Combine(
            root,
            "tools",
            "OpenPlanTrace.Viewer",
            "wwwroot",
            "samples",
            "routing-review-benchmark-draft.json");
        using var benchmarkDocument = JsonDocument.Parse(File.ReadAllText(benchmarkPath));
        var expectations = benchmarkDocument.RootElement
            .GetProperty("fixtures")[0]
            .GetProperty("expectations");

        foreach (var metricName in new[]
                 {
                     "objectAggregateMetrics",
                     "routingBarrierMetrics",
                     "routingPassageMetrics",
                     "routingObstacleMetrics",
                     "routingRoomUseHintMetrics",
                     "routingSuppressedObjectMetrics"
                 })
        {
            Assert.True(expectations.TryGetProperty(metricName, out var metric), $"Missing sample metric '{metricName}'.");
            Assert.True(metric.GetProperty("targets").GetArrayLength() > 0, $"Sample metric '{metricName}' has no targets.");
        }
    }

    [Fact]
    public async Task EmbeddedObjectReviewDatasetSchema_MatchesDocumentedSchemaArtifact()
    {
        var schema = ObjectReviewDatasetJsonSchema.ReadCurrent();
        using var document = JsonDocument.Parse(schema);

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("urn:openplantrace:schema:object-review-dataset:v2", document.RootElement.GetProperty("$id").GetString());
        Assert.Equal(
            ObjectReviewDataset.CurrentSchemaVersion,
            document.RootElement.GetProperty("x-openplantrace-schemaVersion").GetString());

        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "schemas",
            $"{ObjectReviewDataset.CurrentSchemaVersion}.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing documented schema artifact: {schemaPath}");
        Assert.Equal(Normalize(File.ReadAllText(schemaPath)), Normalize(schema));

        await using var stream = new MemoryStream();
        await ObjectReviewDatasetJsonSchema.WriteCurrentAsync(stream);
        Assert.Equal(schema, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void ObjectReviewDatasetSchema_TopLevelContractMatchesJsonSerializer()
    {
        using var schemaDocument = JsonDocument.Parse(ObjectReviewDatasetJsonSchema.ReadCurrent());
        var requiredProperties = ReadStringArray(schemaDocument.RootElement.GetProperty("required"));
        var schemaProperties = schemaDocument.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var datasetProperties = typeof(ObjectReviewDataset)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => ToCamelCase(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var datasetProperty in datasetProperties)
        {
            Assert.Contains(datasetProperty, schemaProperties);
        }

        Assert.Equal(
            new[] { "documentId", "generatedAt", "groups", "schemaVersion", "ungroupedCandidates" },
            requiredProperties.Order(StringComparer.Ordinal).ToArray());

        using var exportedDocument = JsonDocument.Parse(
            ObjectReviewDatasetJsonSerializer.Serialize(
                CreateObjectReviewDataset(),
                writeIndented: false));

        Assert.Equal(
            ObjectReviewDataset.CurrentSchemaVersion,
            exportedDocument.RootElement.GetProperty("schemaVersion").GetString());
        foreach (var requiredProperty in requiredProperties)
        {
            Assert.True(
                exportedDocument.RootElement.TryGetProperty(requiredProperty, out _),
                $"Object review dataset JSON is missing schema-required top-level property '{requiredProperty}'.");
        }
    }

    [Fact]
    public void ObjectReviewDatasetSchema_DefinesReviewEvidenceShapes()
    {
        using var schemaDocument = JsonDocument.Parse(ObjectReviewDatasetJsonSchema.ReadCurrent());

        AssertDefinitionRequires(schemaDocument, "reviewGroup", "groupId", "signature", "candidateIds", "detectedTags", "sourcePrimitiveIds", "sourceLayers", "requiresReview", "confidence", "representativeBounds", "reviewCropBounds", "suggestedRule", "candidates", "nearbyText", "evidence");
        AssertDefinitionRequires(schemaDocument, "reviewCandidate", "candidateId", "pageNumber", "kind", "category", "sourceKind", "sourceWallComponentId", "sourceWallComponentKind", "bounds", "reviewCropBounds", "confidence", "detectedTag", "detectedTagSourcePrimitiveId", "sourcePrimitiveIds", "sourceLayers", "nearbyText", "evidence");
        AssertDefinitionRequires(schemaDocument, "ruleSuggestion", "signature", "category", "kind", "requiresReview", "confidence", "evidence");
        AssertDefinitionRequires(schemaDocument, "textEvidence", "text", "pageNumber", "bounds", "sourcePrimitiveId", "distance");

        var sourceKindEnum = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("reviewCandidate")
            .GetProperty("properties")
            .GetProperty("sourceKind")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var kind in Enum.GetNames<ObjectCandidateSourceKind>())
        {
            Assert.Contains(kind, sourceKindEnum);
        }
    }

    [Fact]
    public async Task EmbeddedKvemoCropManifestSchema_MatchesDocumentedSchemaArtifact()
    {
        var schema = VisualAiCropManifestJsonSchema.ReadCurrent();
        using var document = JsonDocument.Parse(schema);

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("urn:openplantrace:schema:kvemo-crops:v2", document.RootElement.GetProperty("$id").GetString());
        Assert.Equal(
            VisualAiCropManifestEntry.CurrentSchemaVersion,
            document.RootElement.GetProperty("x-openplantrace-schemaVersion").GetString());

        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "schemas",
            $"{VisualAiCropManifestEntry.CurrentSchemaVersion}.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing documented schema artifact: {schemaPath}");
        Assert.Equal(Normalize(File.ReadAllText(schemaPath)), Normalize(schema));

        await using var stream = new MemoryStream();
        await VisualAiCropManifestJsonSchema.WriteCurrentAsync(stream);
        Assert.Equal(schema, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void KvemoCropManifestSchema_DefinesCoordinateProvenanceAndReviewFields()
    {
        using var schemaDocument = JsonDocument.Parse(VisualAiCropManifestJsonSchema.ReadCurrent());

        var required = ReadStringArray(schemaDocument.RootElement.GetProperty("required"));
        foreach (var requiredName in new[]
                 {
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
                     "suggestedTrainingUse"
                 })
        {
            Assert.Contains(requiredName, required);
        }

        AssertDefinitionRequires(schemaDocument, "sourceEvidence", "primitiveCount", "resolvedPrimitiveCount", "primitiveIdsSample", "unresolvedPrimitiveIdsSample", "sourceFormats", "layers", "entityTypes", "blockNames", "colors", "lineTypes", "drawingSpaces");
        AssertDefinitionRequires(schemaDocument, "provenanceCount", "value", "count");
        AssertDefinitionRequires(schemaDocument, "classification", "label", "category", "confidence", "modelName", "modelVersion", "inferenceEngine", "alternatives", "evidence");

        var properties = schemaDocument.RootElement.GetProperty("properties");
        Assert.True(properties.TryGetProperty("sourceKindCounts", out _));
        Assert.True(properties.TryGetProperty("sourceWallComponentIds", out _));
        Assert.True(properties.TryGetProperty("sourceWallComponentKindCounts", out _));

        var sourceKindEnum = schemaDocument.RootElement
            .GetProperty("properties")
            .GetProperty("sourceKind")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var kind in Enum.GetNames<ObjectCandidateSourceKind>())
        {
            Assert.Contains(kind, sourceKindEnum);
        }
    }

    [Fact]
    public async Task EmbeddedObjectCorrectionDatasetSchema_MatchesDocumentedSchemaArtifact()
    {
        var schema = ObjectCorrectionDatasetJsonSchema.ReadCurrent();
        using var document = JsonDocument.Parse(schema);

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("urn:openplantrace:schema:object-correction-dataset:v1", document.RootElement.GetProperty("$id").GetString());
        Assert.Equal(
            ObjectCorrectionDataset.CurrentSchemaVersion,
            document.RootElement.GetProperty("x-openplantrace-schemaVersion").GetString());

        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "schemas",
            $"{ObjectCorrectionDataset.CurrentSchemaVersion}.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing documented schema artifact: {schemaPath}");
        Assert.Equal(Normalize(File.ReadAllText(schemaPath)), Normalize(schema));

        await using var stream = new MemoryStream();
        await ObjectCorrectionDatasetJsonSchema.WriteCurrentAsync(stream);
        Assert.Equal(schema, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void ObjectCorrectionDatasetSchema_TopLevelContractMatchesJsonSerializerAndExample()
    {
        using var schemaDocument = JsonDocument.Parse(ObjectCorrectionDatasetJsonSchema.ReadCurrent());
        var requiredProperties = ReadStringArray(schemaDocument.RootElement.GetProperty("required"));
        var schemaProperties = schemaDocument.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var datasetProperties = typeof(ObjectCorrectionDataset)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => ToCamelCase(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var datasetProperty in datasetProperties)
        {
            Assert.Contains(datasetProperty, schemaProperties);
        }

        Assert.Equal(
            new[] { "actions", "createdAt", "schemaVersion" },
            requiredProperties.Order(StringComparer.Ordinal).ToArray());

        using var exportedDocument = JsonDocument.Parse(
            ObjectCorrectionDatasetJsonSerializer.Serialize(
                CreateObjectCorrectionDataset(),
                writeIndented: false));
        Assert.Equal(
            ObjectCorrectionDataset.CurrentSchemaVersion,
            exportedDocument.RootElement.GetProperty("schemaVersion").GetString());
        foreach (var requiredProperty in requiredProperties)
        {
            Assert.True(
                exportedDocument.RootElement.TryGetProperty(requiredProperty, out _),
                $"Object correction dataset JSON is missing schema-required top-level property '{requiredProperty}'.");
        }

        var examplePath = Path.Combine(FindRepositoryRoot(), "docs", "object-correction-dataset.example.json");
        var exampleDataset = ObjectCorrectionDataset.ParseJson(File.ReadAllText(examplePath));
        Assert.Equal(ObjectCorrectionDataset.CurrentSchemaVersion, exampleDataset.SchemaVersion);
        Assert.NotEmpty(exampleDataset.Actions);
    }

    [Fact]
    public void ObjectCorrectionDatasetSchema_DefinesCorrectionActionShapes()
    {
        using var schemaDocument = JsonDocument.Parse(ObjectCorrectionDatasetJsonSchema.ReadCurrent());

        AssertDefinitionRequires(schemaDocument, "correctionAction", "actionId", "targetKind", "decision", "applyScope", "detectedTags", "candidateIds", "sourcePrimitiveIds", "sourceLayers", "nearbyText", "evidence");
        AssertDefinitionRequires(schemaDocument, "textEvidence", "text", "pageNumber", "bounds", "sourcePrimitiveId", "distance");
        Assert.True(
            schemaDocument.RootElement
                .GetProperty("$defs")
                .GetProperty("correctionAction")
                .GetProperty("properties")
                .TryGetProperty("pageNumbers", out _),
            "Object correction action schema is missing pageNumbers impact metadata.");
        Assert.True(
            schemaDocument.RootElement
                .GetProperty("$defs")
                .GetProperty("correctionAction")
                .GetProperty("properties")
                .TryGetProperty("reviewCropBounds", out _),
            "Object correction action schema is missing reviewCropBounds crop metadata.");

        var decisionEnum = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("decision")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var decision in Enum.GetNames<ObjectCorrectionDecision>())
        {
            Assert.Contains(decision, decisionEnum);
        }

        var applyScopeEnum = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("applyScope")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var applyScope in Enum.GetNames<ObjectCorrectionApplyScope>())
        {
            Assert.Contains(applyScope, applyScopeEnum);
        }
    }

    [Fact]
    public async Task EmbeddedBenchmarkManifestSchema_MatchesDocumentedSchemaArtifact()
    {
        var schema = BenchmarkManifestJsonSchema.ReadCurrent();
        using var document = JsonDocument.Parse(schema);

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("urn:openplantrace:schema:benchmark-manifest:v1", document.RootElement.GetProperty("$id").GetString());
        Assert.Equal(
            BenchmarkManifest.CurrentSchemaVersion,
            document.RootElement.GetProperty("x-openplantrace-schemaVersion").GetString());

        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "schemas",
            $"{BenchmarkManifest.CurrentSchemaVersion}.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing documented schema artifact: {schemaPath}");
        Assert.Equal(Normalize(File.ReadAllText(schemaPath)), Normalize(schema));

        await using var stream = new MemoryStream();
        await BenchmarkManifestJsonSchema.WriteCurrentAsync(stream);
        Assert.Equal(schema, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void BenchmarkManifestSchema_TopLevelContractMatchesBenchmarkManifestModel()
    {
        using var schemaDocument = JsonDocument.Parse(BenchmarkManifestJsonSchema.ReadCurrent());
        var requiredProperties = ReadStringArray(schemaDocument.RootElement.GetProperty("required"));
        var schemaProperties = schemaDocument.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var manifestProperties = typeof(BenchmarkManifest)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => ToCamelCase(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var manifestProperty in manifestProperties)
        {
            Assert.Contains(manifestProperty, schemaProperties);
        }

        Assert.Equal(
            new[] { "fixtures", "schemaVersion" },
            requiredProperties.Order(StringComparer.Ordinal).ToArray());

        using var exportedDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(
                CreateBenchmarkManifest(),
                CreateBenchmarkJsonOptions(writeIndented: false)));

        Assert.Equal(
            BenchmarkManifest.CurrentSchemaVersion,
            exportedDocument.RootElement.GetProperty("schemaVersion").GetString());
        foreach (var requiredProperty in requiredProperties)
        {
            Assert.True(
                exportedDocument.RootElement.TryGetProperty(requiredProperty, out _),
                $"Benchmark manifest JSON is missing schema-required top-level property '{requiredProperty}'.");
        }
    }

    [Fact]
    public void BenchmarkManifestSchema_DefinesFixtureStageAndDetectorMetricShapes()
    {
        using var schemaDocument = JsonDocument.Parse(BenchmarkManifestJsonSchema.ReadCurrent());

        AssertDefinitionRequires(schemaDocument, "fixture", "id", "sourcePath", "expectations");
        AssertDefinitionRequires(schemaDocument, "stageExpectation", "stage");

        var defs = schemaDocument.RootElement.GetProperty("$defs");
        var fixtureProperties = defs
            .GetProperty("fixture")
            .GetProperty("properties");
        foreach (var fixtureProperty in new[] { "optional", "skipReason" })
        {
            Assert.True(fixtureProperties.TryGetProperty(fixtureProperty, out _), $"Missing benchmark fixture property '{fixtureProperty}'.");
        }

        var expectationsProperties = defs
            .GetProperty("expectations")
            .GetProperty("properties");
        foreach (var qualityProperty in new[]
                 {
                     "minQualityGrade",
                     "minQualityConfidence",
                     "maxQualityIssues",
                     "maxScanRiskIssues",
                     "maxScanReviewQueueItems",
                     "maxScanReviewQueueKindCounts",
                     "requiredScanReviewQueueKinds",
                     "forbiddenScanReviewQueueKinds",
                     "minImportReadinessGrade",
                     "minImportReadinessScore",
                     "requireGeometryImportReady",
                     "requireMetricImportReady",
                     "requireRoutingImportReady",
                     "allowImportReview",
                     "minObjectAggregates",
                     "maxObjectAggregates",
                     "minRoutingItems",
                     "maxRoutingItems",
                     "minRoutingSuppressedObjects",
                     "maxRoutingSuppressedObjects",
                     "minSurfacePatterns",
                     "maxSurfacePatterns",
                     "minMeasurementCheckedCount",
                     "minMeasurementConsistentCount",
                     "maxMeasurementOutlierCount",
                     "maxMeasurementOutlierRatio",
                     "maxMeasurementScaleSpreadRatio",
                     "requiredQualityIssueCodes",
                     "forbiddenQualityIssueCodes",
                     "requiredImportIssueCodes",
                     "forbiddenImportIssueCodes"
                 })
        {
            Assert.True(expectationsProperties.TryGetProperty(qualityProperty, out _), $"Missing benchmark quality property '{qualityProperty}'.");
        }

        foreach (var roomGraphProperty in new[] { "minRoomAdjacencies", "maxRoomAdjacencies", "minRoomClusters", "maxRoomClusters" })
        {
            Assert.True(expectationsProperties.TryGetProperty(roomGraphProperty, out _), $"Missing benchmark room-graph property '{roomGraphProperty}'.");
        }

        foreach (var roomLabelProperty in new[] { "requiredRoomLabels", "forbiddenRoomLabels" })
        {
            Assert.True(expectationsProperties.TryGetProperty(roomLabelProperty, out _), $"Missing benchmark room-label property '{roomLabelProperty}'.");
        }

        foreach (var gridBayProperty in new[] { "minGridBaySpacings", "maxGridBaySpacings" })
        {
            Assert.True(expectationsProperties.TryGetProperty(gridBayProperty, out _), $"Missing benchmark grid-bay property '{gridBayProperty}'.");
        }

        foreach (var annotationReferenceProperty in new[] { "minAnnotationReferences", "maxAnnotationReferences" })
        {
            Assert.True(expectationsProperties.TryGetProperty(annotationReferenceProperty, out _), $"Missing benchmark annotation-reference property '{annotationReferenceProperty}'.");
        }

        foreach (var metricName in new[]
                 {
                     "regionMetrics",
                     "dimensionMetrics",
                     "annotationMetrics",
                     "annotationReferenceMetrics",
                     "gridAxisMetrics",
                     "wallMetrics",
                     "roomMetrics",
                     "openingMetrics",
                     "objectMetrics",
                     "objectGroupMetrics",
                     "objectAggregateMetrics",
                     "routingBarrierMetrics",
                     "routingPassageMetrics",
                     "routingObstacleMetrics",
                     "routingRoomUseHintMetrics",
                     "routingSuppressedObjectMetrics",
                     "layerMetrics"
                 })
        {
            Assert.True(expectationsProperties.TryGetProperty(metricName, out _), $"Missing benchmark metric block '{metricName}'.");
        }

        var metricProperties = defs
            .GetProperty("metricExpectations")
            .GetProperty("properties");
        Assert.True(metricProperties.TryGetProperty("targets", out _));
        Assert.True(metricProperties.TryGetProperty("minRecall", out _));
        Assert.True(metricProperties.TryGetProperty("minPrecision", out _));
        Assert.True(metricProperties.TryGetProperty("completeTruthSet", out _));

        var targetProperties = defs
            .GetProperty("detectionTarget")
            .GetProperty("properties");
        foreach (var targetProperty in new[]
                 {
                     "id",
                     "pageNumber",
                     "bounds",
                     "label",
                     "text",
                     "marker",
                     "minCount",
                     "requiresReview",
                     "regionKind",
                     "dimensionKind",
                     "dimensionOrientation",
                     "annotationKind",
                     "gridAxisOrientation",
                     "openingType",
                     "openingOperation",
                     "objectCategory",
                     "objectKind",
                     "detectedTags",
                     "layerCategory",
                     "routingSourceKind",
                     "routingObstacleKind",
                     "routingInfluence",
                     "structuralInfluence",
                     "roomUseKind",
                     "suppressesChildObjects",
                     "objectCandidateId",
                     "suppressedByAggregateId",
                     "suppressionReason",
                     "suppressionAction",
                     "replacementRoutingObstacleId",
                     "roomUseHintId",
                     "confidence",
                     "sourcePrimitiveIds",
                     "sourceLayers",
                     "evidence"
                 })
        {
            Assert.True(targetProperties.TryGetProperty(targetProperty, out _), $"Missing benchmark target property '{targetProperty}'.");
        }

        var qualityGradeEnum = defs
            .GetProperty("scanQualityGrade")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var grade in Enum.GetNames<PlanScanQualityGrade>())
        {
            Assert.Contains(grade, qualityGradeEnum);
        }

        foreach (var definitionAndEnum in new (string Definition, Type EnumType)[]
                 {
                     ("objectKind", typeof(ObjectCandidateKind)),
                     ("objectRoutingInfluence", typeof(ObjectRoutingInfluence)),
                     ("objectStructuralInfluence", typeof(ObjectStructuralInfluence)),
                     ("roomUseKind", typeof(RoomUseKind)),
                     ("routingObstacleKind", typeof(RoutingObstacleKind)),
                     ("routingSourceKind", typeof(RoutingSourceKind)),
                     ("routingSuppressionReason", typeof(RoutingSuppressionReason)),
                     ("routingSuppressedObjectAction", typeof(RoutingSuppressedObjectAction))
                 })
        {
            var enumValues = defs
                .GetProperty(definitionAndEnum.Definition)
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray();

            foreach (var name in Enum.GetNames(definitionAndEnum.EnumType))
            {
                Assert.Contains(name, enumValues);
            }
        }
    }

    [Fact]
    public async Task EmbeddedBenchmarkRunResultSchema_MatchesDocumentedSchemaArtifact()
    {
        var schema = BenchmarkRunResultJsonSchema.ReadCurrent();
        using var document = JsonDocument.Parse(schema);

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("urn:openplantrace:schema:benchmark-result:v1", document.RootElement.GetProperty("$id").GetString());
        Assert.Equal(
            BenchmarkRunResult.CurrentSchemaVersion,
            document.RootElement.GetProperty("x-openplantrace-schemaVersion").GetString());

        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "schemas",
            $"{BenchmarkRunResult.CurrentSchemaVersion}.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing documented schema artifact: {schemaPath}");
        Assert.Equal(Normalize(File.ReadAllText(schemaPath)), Normalize(schema));

        await using var stream = new MemoryStream();
        await BenchmarkRunResultJsonSchema.WriteCurrentAsync(stream);
        Assert.Equal(schema, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void BenchmarkRunResultSchema_TopLevelContractMatchesRunModel()
    {
        using var schemaDocument = JsonDocument.Parse(BenchmarkRunResultJsonSchema.ReadCurrent());
        var requiredProperties = ReadStringArray(schemaDocument.RootElement.GetProperty("required"));
        var schemaProperties = schemaDocument.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var resultProperties = typeof(BenchmarkRunResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => ToCamelCase(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var resultProperty in resultProperties)
        {
            Assert.Contains(resultProperty, schemaProperties);
        }

        Assert.Equal(
            new[]
            {
                "caseCount",
                "cases",
                "failedAssertionCount",
                "failedCaseCount",
                "generatedAt",
                "passed",
                "passedAssertionCount",
                "passedCaseCount",
                "reviewQueue",
                "reviewQueueCount",
                "schemaVersion",
                "scoreboard",
                "skippedCaseCount"
            },
            requiredProperties.Order(StringComparer.Ordinal).ToArray());

        using var exportedDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(
                CreateBenchmarkRunResult(),
                CreateBenchmarkJsonOptions(writeIndented: false)));

        Assert.Equal(
            BenchmarkRunResult.CurrentSchemaVersion,
            exportedDocument.RootElement.GetProperty("schemaVersion").GetString());
        foreach (var requiredProperty in requiredProperties)
        {
            Assert.True(
                exportedDocument.RootElement.TryGetProperty(requiredProperty, out _),
                $"Benchmark result JSON is missing schema-required top-level property '{requiredProperty}'.");
        }
    }

    [Fact]
    public void BenchmarkRunResultSchema_DefinesScoreboardAndMetricShapes()
    {
        using var schemaDocument = JsonDocument.Parse(BenchmarkRunResultJsonSchema.ReadCurrent());

        AssertDefinitionRequires(schemaDocument, "scoreboard", "schemaVersion", "generatedAt", "grade", "overallScore", "consumerReadinessScore", "readyForDownstreamUse", "caseCount", "scoredCaseCount", "skippedCaseCount", "failedScanCount", "failedAssertionCount", "expectedTargetCount", "matchedTargetCount", "missedTargetCount", "extraDetectionCount", "cases", "detectors", "failureBuckets", "recommendedNextActions");
        AssertDefinitionRequires(schemaDocument, "caseResult", "fixtureId", "sourcePath", "passed", "scanSucceeded", "durationMilliseconds", "counts", "assertions", "metrics", "qualityIssues", "diagnosticIssues", "stages", "importReadiness", "passedAssertionCount", "failedAssertionCount");
        AssertDefinitionRequires(schemaDocument, "caseScore", "fixtureId", "grade", "overallScore", "targetF1", "targetRecall", "targetPrecision", "assertionReliability", "scanQualityScore", "measurementScore", "importReadinessScore", "readyForGeometryImport", "readyForMetricImport", "readyForRoutingImport", "expectedTargetCount", "matchedTargetCount", "missedTargetCount", "extraDetectionCount", "failedAssertionCount", "qualityGrade", "qualityConfidence", "requiresReview", "skipped", "blockingReasons");
        AssertDefinitionRequires(schemaDocument, "importReadiness", "grade", "score", "readyForGeometryImport", "readyForMetricImport", "readyForRoutingImport", "requiresReview", "blockingIssueCodes", "reviewIssueCodes", "recommendedActions", "evidence");
        AssertDefinitionRequires(schemaDocument, "scanReviewQueueSummary", "count", "kindCounts", "severityCounts");
        AssertDefinitionRequires(schemaDocument, "caseIssueSummary", "code", "severity", "stage", "scope", "count", "message", "pageNumbers", "maxConfidence", "sourcePrimitiveCount", "sourcePrimitiveIds", "properties");
        AssertDefinitionRequires(schemaDocument, "stageSummary", "stage", "durationMilliseconds", "inputCount", "outputCount", "diagnosticCount", "infoCount", "warningCount", "errorCount");
        AssertDefinitionRequires(schemaDocument, "detectorMetric", "detector", "expectedCount", "detectedCount", "matchedCount", "missedCount", "extraCount", "recall", "precision", "f1", "matches", "precisionScoringEnabled", "extraDetections", "reviewOnlyDetections");
        AssertDefinitionRequires(schemaDocument, "reviewQueueItem", "fixtureId", "sourcePath", "detector", "kind", "precisionScoringEnabled", "detection", "recommendedAction");
        AssertDefinitionRequires(schemaDocument, "detectionSummary", "detectionId", "detectedTags", "evidence");
        AssertDefinitionRequires(schemaDocument, "failureBucket", "code", "severity", "count", "message", "evidence", "targetIds");

        var defs = schemaDocument.RootElement.GetProperty("$defs");
        var caseResultProperties = defs
            .GetProperty("caseResult")
            .GetProperty("properties");
        Assert.True(caseResultProperties.TryGetProperty("scanReviewQueue", out _), "Missing benchmark case scan review queue summary property.");
        Assert.True(
            defs.GetProperty("counts").GetProperty("properties").TryGetProperty("surfacePatterns", out _),
            "Benchmark result counts schema should document optional surfacePatterns.");

        var scoreGradeEnum = defs
            .GetProperty("scoreGrade")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var grade in Enum.GetNames<BenchmarkScoreGrade>())
        {
            Assert.Contains(grade, scoreGradeEnum);
        }

        var failureSeverityEnum = defs
            .GetProperty("failureSeverity")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var severity in Enum.GetNames<BenchmarkFailureSeverity>())
        {
            Assert.Contains(severity, failureSeverityEnum);
        }

        var reviewKindEnum = defs
            .GetProperty("reviewQueueKind")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var kind in Enum.GetNames<BenchmarkReviewQueueKind>())
        {
            Assert.Contains(kind, reviewKindEnum);
        }

        var diagnosticSeverityEnum = defs
            .GetProperty("diagnosticSeverity")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var severity in Enum.GetNames<DiagnosticSeverity>())
        {
            Assert.Contains(severity, diagnosticSeverityEnum);
        }

        var qualityGradeEnum = defs
            .GetProperty("qualityGrade")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var grade in Enum.GetNames<PlanScanQualityGrade>())
        {
            Assert.Contains(grade, qualityGradeEnum);
        }
    }

    [Fact]
    public async Task EmbeddedBenchmarkComparisonSchema_MatchesDocumentedSchemaArtifact()
    {
        var schema = BenchmarkComparisonJsonSchema.ReadCurrent();
        using var document = JsonDocument.Parse(schema);

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("urn:openplantrace:schema:benchmark-comparison:v1", document.RootElement.GetProperty("$id").GetString());
        Assert.Equal(
            BenchmarkComparisonResult.CurrentSchemaVersion,
            document.RootElement.GetProperty("x-openplantrace-schemaVersion").GetString());

        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "schemas",
            $"{BenchmarkComparisonResult.CurrentSchemaVersion}.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing documented schema artifact: {schemaPath}");
        Assert.Equal(Normalize(File.ReadAllText(schemaPath)), Normalize(schema));

        await using var stream = new MemoryStream();
        await BenchmarkComparisonJsonSchema.WriteCurrentAsync(stream);
        Assert.Equal(schema, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void BenchmarkComparisonSchema_TopLevelContractMatchesComparisonModel()
    {
        using var schemaDocument = JsonDocument.Parse(BenchmarkComparisonJsonSchema.ReadCurrent());
        var requiredProperties = ReadStringArray(schemaDocument.RootElement.GetProperty("required"));
        var schemaProperties = schemaDocument.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var comparisonProperties = typeof(BenchmarkComparisonResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => ToCamelCase(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var comparisonProperty in comparisonProperties)
        {
            Assert.Contains(comparisonProperty, schemaProperties);
        }

        Assert.Equal(
            new[]
            {
                "addedCaseCount",
                "baselineCaseCount",
                "candidateCaseCount",
                "cases",
                "generatedAt",
                "improvementCount",
                "matchedCaseCount",
                "regressionCount",
                "removedCaseCount",
                "schemaVersion",
                "signals"
            },
            requiredProperties.Order(StringComparer.Ordinal).ToArray());

        using var exportedDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(
                CreateBenchmarkComparison(),
                CreateBenchmarkJsonOptions(writeIndented: false)));

        Assert.Equal(
            BenchmarkComparisonResult.CurrentSchemaVersion,
            exportedDocument.RootElement.GetProperty("schemaVersion").GetString());
        foreach (var requiredProperty in requiredProperties)
        {
            Assert.True(
                exportedDocument.RootElement.TryGetProperty(requiredProperty, out _),
                $"Benchmark comparison JSON is missing schema-required top-level property '{requiredProperty}'.");
        }
    }

    [Fact]
    public void BenchmarkComparisonSchema_DefinesCaseSignalAndDeltaShapes()
    {
        using var schemaDocument = JsonDocument.Parse(BenchmarkComparisonJsonSchema.ReadCurrent());

        AssertDefinitionRequires(schemaDocument, "caseComparison", "fixtureId", "status", "countDeltas", "signals");
        AssertDefinitionRequires(schemaDocument, "countDelta", "name");
        AssertDefinitionRequires(schemaDocument, "signal", "fixtureId", "code", "severity", "message");

        var defs = schemaDocument.RootElement.GetProperty("$defs");
        var caseProperties = defs
            .GetProperty("caseComparison")
            .GetProperty("properties");
        foreach (var skippedProperty in new[] { "baselineSkipped", "candidateSkipped", "baselineSkipReason", "candidateSkipReason" })
        {
            Assert.True(caseProperties.TryGetProperty(skippedProperty, out _), $"Missing benchmark comparison skipped property '{skippedProperty}'.");
        }

        var statusEnum = defs
            .GetProperty("caseStatus")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var status in Enum.GetNames<BenchmarkComparisonCaseStatus>())
        {
            Assert.Contains(status, statusEnum);
        }

        var severityEnum = defs
            .GetProperty("severity")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var severity in Enum.GetNames<BenchmarkComparisonSignalSeverity>())
        {
            Assert.Contains(severity, severityEnum);
        }
    }

    [Fact]
    public async Task EmbeddedBatchComparisonSchema_MatchesDocumentedSchemaArtifact()
    {
        var schema = BatchScanComparisonJsonSchema.ReadCurrent();
        using var document = JsonDocument.Parse(schema);

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("urn:openplantrace:schema:batch-comparison:v1", document.RootElement.GetProperty("$id").GetString());
        Assert.Equal(
            BatchScanComparisonResult.CurrentSchemaVersion,
            document.RootElement.GetProperty("x-openplantrace-schemaVersion").GetString());

        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "schemas",
            $"{BatchScanComparisonResult.CurrentSchemaVersion}.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing documented schema artifact: {schemaPath}");
        Assert.Equal(Normalize(File.ReadAllText(schemaPath)), Normalize(schema));

        await using var stream = new MemoryStream();
        await BatchScanComparisonJsonSchema.WriteCurrentAsync(stream);
        Assert.Equal(schema, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void BatchComparisonSchema_TopLevelContractMatchesComparisonModel()
    {
        using var schemaDocument = JsonDocument.Parse(BatchScanComparisonJsonSchema.ReadCurrent());
        var requiredProperties = ReadStringArray(schemaDocument.RootElement.GetProperty("required"));
        var schemaProperties = schemaDocument.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var comparisonProperties = typeof(BatchScanComparisonResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => ToCamelCase(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var comparisonProperty in comparisonProperties)
        {
            Assert.Contains(comparisonProperty, schemaProperties);
        }

        Assert.Equal(
            new[]
            {
                "addedItemCount",
                "baselineItemCount",
                "baselineOutputDirectory",
                "candidateItemCount",
                "candidateOutputDirectory",
                "diagnosticErrorDelta",
                "diagnosticWarningDelta",
                "generatedAt",
                "improvementCount",
                "infoCount",
                "items",
                "matchedItemCount",
                "qualityConfidenceAverageDelta",
                "regressionCount",
                "removedItemCount",
                "schemaVersion",
                "signals",
                "statusChangeCount",
                "totalDurationDeltaMilliseconds",
                "visualErrorIssueDelta",
                "visualIssueDelta"
            },
            requiredProperties.Order(StringComparer.Ordinal).ToArray());

        using var exportedDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(
                CreateBatchComparison(),
                CreateBenchmarkJsonOptions(writeIndented: false)));

        Assert.Equal(
            BatchScanComparisonResult.CurrentSchemaVersion,
            exportedDocument.RootElement.GetProperty("schemaVersion").GetString());
        foreach (var requiredProperty in requiredProperties)
        {
            Assert.True(
                exportedDocument.RootElement.TryGetProperty(requiredProperty, out _),
                $"Batch comparison JSON is missing schema-required top-level property '{requiredProperty}'.");
        }
    }

    [Fact]
    public void BatchComparisonSchema_DefinesItemSignalDeltaAndEvidenceShapes()
    {
        using var schemaDocument = JsonDocument.Parse(BatchScanComparisonJsonSchema.ReadCurrent());

        AssertDefinitionRequires(
            schemaDocument,
            "itemComparison",
            "key",
            "status",
            "baselineInputPath",
            "candidateInputPath",
            "baselineFileName",
            "candidateFileName",
            "baselineScanJsonPath",
            "candidateScanJsonPath",
            "baselineVisualSnapshotPath",
            "candidateVisualSnapshotPath",
            "baselineGeoJsonPath",
            "candidateGeoJsonPath",
            "baselineOverlayDirectory",
            "candidateOverlayDirectory",
            "baselineStatus",
            "candidateStatus",
            "deltas",
            "addedVisualIssueCodes",
            "removedVisualIssueCodes",
            "signals");
        var itemComparisonProperties = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("itemComparison")
            .GetProperty("properties");
        Assert.True(itemComparisonProperties.TryGetProperty("baselinePlacementJsonPath", out _));
        Assert.True(itemComparisonProperties.TryGetProperty("candidatePlacementJsonPath", out _));
        AssertDefinitionRequires(schemaDocument, "metricDelta", "name", "baseline", "candidate", "delta", "unit");
        AssertDefinitionRequires(schemaDocument, "signal", "key", "code", "severity", "message", "baseline", "candidate");

        var defs = schemaDocument.RootElement.GetProperty("$defs");
        var statusEnum = defs
            .GetProperty("comparisonItemStatus")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var status in Enum.GetNames<BatchScanComparisonItemStatus>())
        {
            Assert.Contains(status, statusEnum);
        }

        var batchStatusEnum = defs
            .GetProperty("batchItemStatus")
            .GetProperty("enum")
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .ToArray();
        foreach (var status in Enum.GetNames<BatchScanItemStatus>())
        {
            Assert.Contains(status, batchStatusEnum);
        }

        var severityEnum = defs
            .GetProperty("signalSeverity")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var severity in Enum.GetNames<BatchScanComparisonSignalSeverity>())
        {
            Assert.Contains(severity, severityEnum);
        }
    }

    [Fact]
    public async Task EmbeddedViewerBenchmarkReviewSessionSchema_MatchesDocumentedSchemaArtifact()
    {
        var schema = ViewerBenchmarkReviewSessionJsonSchema.ReadCurrent();
        using var document = JsonDocument.Parse(schema);

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("urn:openplantrace:schema:viewer-benchmark-review-session:v1", document.RootElement.GetProperty("$id").GetString());
        Assert.Equal(
            ViewerBenchmarkReviewSessionJsonSchema.CurrentSchemaVersion,
            document.RootElement.GetProperty("x-openplantrace-schemaVersion").GetString());

        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "schemas",
            $"{ViewerBenchmarkReviewSessionJsonSchema.CurrentSchemaVersion}.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing documented schema artifact: {schemaPath}");
        Assert.Equal(Normalize(File.ReadAllText(schemaPath)), Normalize(schema));

        await using var stream = new MemoryStream();
        await ViewerBenchmarkReviewSessionJsonSchema.WriteCurrentAsync(stream);
        Assert.Equal(schema, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void ViewerBenchmarkReviewSessionSchema_DefinesMergeContractAndExample()
    {
        using var schemaDocument = JsonDocument.Parse(ViewerBenchmarkReviewSessionJsonSchema.ReadCurrent());
        var requiredProperties = ReadStringArray(schemaDocument.RootElement.GetProperty("required"));

        Assert.Equal(
            new[]
            {
                "addedTargets",
                "boundsEdits",
                "decisions",
                "deletedTargets",
                "exportedAt",
                "manifest",
                "reviewIssues",
                "scan",
                "schemaVersion",
                "summary",
                "tool"
            },
            requiredProperties.Order(StringComparer.Ordinal).ToArray());

        AssertDefinitionRequires(schemaDocument, "manifestSnapshot", "schemaVersion", "fixtureCount", "targetCount");
        AssertDefinitionRequires(schemaDocument, "scanSnapshot", "documentId", "pageCount", "qualityGrade", "qualityConfidence", "diagnostics");
        AssertDefinitionRequires(schemaDocument, "diagnosticsSnapshot", "infoCount", "warningCount", "errorCount", "stageCount", "durationMilliseconds");
        AssertDefinitionRequires(schemaDocument, "sessionSummary", "activeTargetCount", "filteredTargetCount", "acceptedCount", "rejectedCount", "needsReviewCount", "unreviewedCount", "addedTargetCount", "deletedTargetCount", "boundsEditedCount", "missingBoundsCount", "lowConfidenceCount", "missingEvidenceCount", "filters");
        AssertDefinitionRequires(schemaDocument, "filters", "query", "detector", "status", "issue", "page");
        AssertDefinitionRequires(schemaDocument, "targetSummary", "reviewKey", "id", "detectorKey", "detectorLabel", "fixtureId", "fixtureName", "pageNumber", "bounds", "originalPageNumber", "originalBounds", "confidence", "decision", "isAdded", "boundsEdited", "hasEvidence", "sourceLayers", "sourcePrimitiveIds", "evidence", "criteria");
        AssertDefinitionRequires(schemaDocument, "boundsEdit", "reviewKey", "pageNumber", "bounds", "editedAt");

        var decisionEnum = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("decision")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Contains("accepted", decisionEnum);
        Assert.Contains("rejected", decisionEnum);
        Assert.Contains("needsReview", decisionEnum);
        Assert.Contains("unreviewed", decisionEnum);

        var detectorKeyEnum = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("detectorKey")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var detectorKey in new[]
                 {
                     "objectAggregateMetrics",
                     "routingBarrierMetrics",
                     "routingPassageMetrics",
                     "routingObstacleMetrics",
                     "routingRoomUseHintMetrics",
                     "routingSuppressedObjectMetrics"
                 })
        {
            Assert.Contains(detectorKey, detectorKeyEnum);
        }

        var examplePath = Path.Combine(FindRepositoryRoot(), "docs", "viewer-benchmark-review-session.example.json");
        using var exampleDocument = JsonDocument.Parse(File.ReadAllText(examplePath));
        Assert.Equal(
            ViewerBenchmarkReviewSessionJsonSchema.CurrentSchemaVersion,
            exampleDocument.RootElement.GetProperty("schemaVersion").GetString());
        foreach (var requiredProperty in requiredProperties)
        {
            Assert.True(
                exampleDocument.RootElement.TryGetProperty(requiredProperty, out _),
                $"Viewer benchmark review-session example is missing schema-required top-level property '{requiredProperty}'.");
        }

        var summary = exampleDocument.RootElement.GetProperty("summary");
        Assert.Equal(
            exampleDocument.RootElement.GetProperty("addedTargets").GetArrayLength(),
            summary.GetProperty("addedTargetCount").GetInt32());
        Assert.Equal(
            exampleDocument.RootElement.GetProperty("deletedTargets").GetArrayLength(),
            summary.GetProperty("deletedTargetCount").GetInt32());
        Assert.Equal(
            exampleDocument.RootElement.GetProperty("boundsEdits").GetArrayLength(),
            summary.GetProperty("boundsEditedCount").GetInt32());

        var addedTarget = exampleDocument.RootElement.GetProperty("addedTargets").EnumerateArray().Single();
        Assert.True(addedTarget.GetProperty("isAdded").GetBoolean());
        Assert.True(addedTarget.TryGetProperty("manifestTarget", out _));
    }

    [Fact]
    public async Task EmbeddedBatchManifestSchema_MatchesDocumentedSchemaArtifact()
    {
        var schema = BatchScanManifestJsonSchema.ReadCurrent();
        using var document = JsonDocument.Parse(schema);

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("urn:openplantrace:schema:batch-manifest:v1", document.RootElement.GetProperty("$id").GetString());
        Assert.Equal(
            BatchScanManifest.CurrentSchemaVersion,
            document.RootElement.GetProperty("x-openplantrace-schemaVersion").GetString());

        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "schemas",
            $"{BatchScanManifest.CurrentSchemaVersion}.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing documented schema artifact: {schemaPath}");
        Assert.Equal(Normalize(File.ReadAllText(schemaPath)), Normalize(schema));

        await using var stream = new MemoryStream();
        await BatchScanManifestJsonSchema.WriteCurrentAsync(stream);
        Assert.Equal(schema, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void BatchManifestSchema_TopLevelContractMatchesBatchManifestModelAndExample()
    {
        using var schemaDocument = JsonDocument.Parse(BatchScanManifestJsonSchema.ReadCurrent());
        var requiredProperties = ReadStringArray(schemaDocument.RootElement.GetProperty("required"));
        var schemaProperties = schemaDocument.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var manifestProperties = typeof(BatchScanManifest)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => ToCamelCase(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var manifestProperty in manifestProperties)
        {
            Assert.Contains(manifestProperty, schemaProperties);
        }

        Assert.Equal(
            new[] { "inputs", "schemaVersion" },
            requiredProperties.Order(StringComparer.Ordinal).ToArray());

        using var exportedDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(
                CreateBatchManifest(),
                CreateProfileJsonOptions(writeIndented: false)));

        Assert.Equal(
            BatchScanManifest.CurrentSchemaVersion,
            exportedDocument.RootElement.GetProperty("schemaVersion").GetString());
        foreach (var requiredProperty in requiredProperties)
        {
            Assert.True(
                exportedDocument.RootElement.TryGetProperty(requiredProperty, out _),
                $"Batch manifest JSON is missing schema-required top-level property '{requiredProperty}'.");
        }

        var examplePath = Path.Combine(FindRepositoryRoot(), "docs", "batch-manifest.example.json");
        var exampleManifest = JsonSerializer.Deserialize<BatchScanManifest>(
            File.ReadAllText(examplePath),
            CreateProfileJsonOptions(writeIndented: false));
        Assert.NotNull(exampleManifest);
        BatchScanManifest.ValidateSchemaVersion(exampleManifest!);
        Assert.NotEmpty(exampleManifest!.Inputs);
    }

    [Fact]
    public void BatchManifestSchema_DefinesProfileOverrideAndScannerOptionShapes()
    {
        using var schemaDocument = JsonDocument.Parse(BatchScanManifestJsonSchema.ReadCurrent());

        AssertDefinitionRequires(schemaDocument, "layerCategoryOverride", "pattern", "category");

        var topLevelProperties = schemaDocument.RootElement.GetProperty("properties");
        Assert.True(topLevelProperties.TryGetProperty("maxDegreeOfParallelism", out _));
        Assert.True(topLevelProperties.TryGetProperty("retryCount", out _));

        var scannerOptionsProperties = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("scannerOptions")
            .GetProperty("properties");
        foreach (var propertyName in new[]
                 {
                     "sheetMargin",
                     "minWallLength",
                     "minWallFragmentLength",
                     "maxWallFragmentGap",
                     "maxWallCandidateSeedsPerPage",
                     "wallMergeTolerance",
                     "wallSnapTolerance",
                     "wallThickness",
                     "minOpeningGap",
                     "maxOpeningGap",
                     "objectNearbyTextSearchRadius",
                     "maxNearbyTextPerObject"
                 })
        {
            Assert.True(scannerOptionsProperties.TryGetProperty(propertyName, out _), $"Missing batch scanner option '{propertyName}'.");
        }

        var categoryEnum = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("layerCategory")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var category in Enum.GetNames<LayerCategory>())
        {
            Assert.Contains(category, categoryEnum);
        }
    }

    [Fact]
    public void DocumentedBatchResultSchema_DefinesVisualSnapshotSummaryContract()
    {
        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "schemas",
            "openplantrace.batch.v5.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing documented schema artifact: {schemaPath}");

        using var schemaDocument = JsonDocument.Parse(File.ReadAllText(schemaPath));

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schemaDocument.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("urn:openplantrace:schema:batch:v5", schemaDocument.RootElement.GetProperty("$id").GetString());
        Assert.Equal("openplantrace.batch.v5", schemaDocument.RootElement.GetProperty("x-openplantrace-schemaVersion").GetString());
        Assert.Equal(
            new[] { "generatedAt", "items", "maxDegreeOfParallelism", "outputDirectory", "retryCount", "schemaVersion" },
            ReadStringArray(schemaDocument.RootElement.GetProperty("required")).Order(StringComparer.Ordinal).ToArray());

        AssertDefinitionRequires(schemaDocument, "batchItem", "itemNumber", "inputPath", "sourceKind", "effectiveSourceKind", "status", "attemptCount", "durationMilliseconds", "counts", "scanJsonPath", "geoJsonPath", "overlayDirectory", "visualSnapshotPath", "visualSnapshot", "errorMessage", "sourceCapability");
        Assert.True(
            schemaDocument.RootElement
                .GetProperty("$defs")
                .GetProperty("batchItem")
                .GetProperty("properties")
                .TryGetProperty("placementJsonPath", out _),
            "Batch item schema should document placementJsonPath.");
        AssertDefinitionRequires(schemaDocument, "visualSnapshotSummary", "schemaVersion", "pageCount", "layerCount", "drawableItemCount", "issueCount", "warningIssueCount", "errorIssueCount", "maxDetectionCoverage", "issueCodes");
        AssertDefinitionRequires(schemaDocument, "scanCounts", "pages", "regions", "titleBlocks", "dimensions", "annotations", "gridAxes", "gridBaySpacings", "walls", "wallNodes", "wallEdges", "rooms", "roomAdjacencies", "roomClusters", "openings", "objects", "objectGroups", "objectAggregates", "routingItems", "diagnostics", "diagnosticWarnings", "diagnosticErrors", "qualityGrade", "qualityConfidence", "requiresReview");
        Assert.True(
            schemaDocument.RootElement
                .GetProperty("$defs")
                .GetProperty("scanCounts")
                .GetProperty("properties")
                .TryGetProperty("surfacePatterns", out _),
            "Batch scan counts schema should document optional surfacePatterns.");

        var sourceKindEnum = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("sourceKind")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var kind in Enum.GetNames<PlanSourceKind>())
        {
            Assert.Contains(kind, sourceKindEnum);
        }
    }

    [Fact]
    public async Task EmbeddedLayerCategoryProfileSchema_MatchesDocumentedSchemaArtifact()
    {
        var schema = LayerCategoryProfileJsonSchema.ReadCurrent();
        using var document = JsonDocument.Parse(schema);

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("urn:openplantrace:schema:layer-profile:v1", document.RootElement.GetProperty("$id").GetString());
        Assert.Equal(
            LayerCategoryProfile.CurrentSchemaVersion,
            document.RootElement.GetProperty("x-openplantrace-schemaVersion").GetString());

        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "schemas",
            $"{LayerCategoryProfile.CurrentSchemaVersion}.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing documented schema artifact: {schemaPath}");
        Assert.Equal(Normalize(File.ReadAllText(schemaPath)), Normalize(schema));

        await using var stream = new MemoryStream();
        await LayerCategoryProfileJsonSchema.WriteCurrentAsync(stream);
        Assert.Equal(schema, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void LayerCategoryProfileSchema_TopLevelContractMatchesProfileModelAndExample()
    {
        using var schemaDocument = JsonDocument.Parse(LayerCategoryProfileJsonSchema.ReadCurrent());
        var requiredProperties = ReadStringArray(schemaDocument.RootElement.GetProperty("required"));
        var schemaProperties = schemaDocument.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var profileProperties = typeof(LayerCategoryProfile)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => ToCamelCase(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var profileProperty in profileProperties)
        {
            Assert.Contains(profileProperty, schemaProperties);
        }

        Assert.Equal(
            new[] { "overrides", "schemaVersion" },
            requiredProperties.Order(StringComparer.Ordinal).ToArray());

        var serialized = JsonSerializer.Serialize(
            CreateLayerCategoryProfile(),
            CreateProfileJsonOptions(writeIndented: false));
        using var exportedDocument = JsonDocument.Parse(serialized);
        Assert.Equal(
            LayerCategoryProfile.CurrentSchemaVersion,
            exportedDocument.RootElement.GetProperty("schemaVersion").GetString());
        foreach (var requiredProperty in requiredProperties)
        {
            Assert.True(
                exportedDocument.RootElement.TryGetProperty(requiredProperty, out _),
                $"Layer profile JSON is missing schema-required top-level property '{requiredProperty}'.");
        }

        var examplePath = Path.Combine(FindRepositoryRoot(), "docs", "layer-profile.example.json");
        var exampleProfile = LayerCategoryProfile.ParseJson(File.ReadAllText(examplePath));
        Assert.Equal(LayerCategoryProfile.CurrentSchemaVersion, exampleProfile.SchemaVersion);
        Assert.NotEmpty(exampleProfile.Overrides);
    }

    [Fact]
    public void LayerCategoryProfileSchema_DefinesOverrideShape()
    {
        using var schemaDocument = JsonDocument.Parse(LayerCategoryProfileJsonSchema.ReadCurrent());

        AssertDefinitionRequires(schemaDocument, "layerOverride", "pattern", "category");

        var categoryEnum = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty("layerCategory")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var category in Enum.GetNames<LayerCategory>())
        {
            Assert.Contains(category, categoryEnum);
        }
    }

    [Fact]
    public async Task EmbeddedObjectLabelProfileSchema_MatchesDocumentedSchemaArtifact()
    {
        var schema = ObjectLabelProfileJsonSchema.ReadCurrent();
        using var document = JsonDocument.Parse(schema);

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("urn:openplantrace:schema:object-label-profile:v1", document.RootElement.GetProperty("$id").GetString());
        Assert.Equal(
            ObjectLabelProfile.CurrentSchemaVersion,
            document.RootElement.GetProperty("x-openplantrace-schemaVersion").GetString());

        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "schemas",
            $"{ObjectLabelProfile.CurrentSchemaVersion}.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing documented schema artifact: {schemaPath}");
        Assert.Equal(Normalize(File.ReadAllText(schemaPath)), Normalize(schema));

        await using var stream = new MemoryStream();
        await ObjectLabelProfileJsonSchema.WriteCurrentAsync(stream);
        Assert.Equal(schema, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void ObjectLabelProfileSchema_TopLevelContractMatchesSerializerAndExample()
    {
        using var schemaDocument = JsonDocument.Parse(ObjectLabelProfileJsonSchema.ReadCurrent());
        var requiredProperties = ReadStringArray(schemaDocument.RootElement.GetProperty("required"));
        var schemaProperties = schemaDocument.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var profileProperties = typeof(ObjectLabelProfile)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => ToCamelCase(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var profileProperty in profileProperties)
        {
            Assert.Contains(profileProperty, schemaProperties);
        }

        Assert.Equal(
            new[] { "rules", "schemaVersion" },
            requiredProperties.Order(StringComparer.Ordinal).ToArray());

        using var exportedDocument = JsonDocument.Parse(
            ObjectLabelProfileJsonSerializer.Serialize(
                CreateObjectLabelProfile(),
                writeIndented: false));
        Assert.Equal(
            ObjectLabelProfile.CurrentSchemaVersion,
            exportedDocument.RootElement.GetProperty("schemaVersion").GetString());
        foreach (var requiredProperty in requiredProperties)
        {
            Assert.True(
                exportedDocument.RootElement.TryGetProperty(requiredProperty, out _),
                $"Object label profile JSON is missing schema-required top-level property '{requiredProperty}'.");
        }

        var examplePath = Path.Combine(FindRepositoryRoot(), "docs", "object-label-profile.example.json");
        var exampleProfile = ObjectLabelProfile.ParseJson(File.ReadAllText(examplePath));
        Assert.Equal(ObjectLabelProfile.CurrentSchemaVersion, exampleProfile.SchemaVersion);
        Assert.NotEmpty(exampleProfile.Rules);
    }

    [Fact]
    public void ObjectLabelProfileSchema_DefinesRuleSelectorOutputAndEnumShapes()
    {
        using var schemaDocument = JsonDocument.Parse(ObjectLabelProfileJsonSchema.ReadCurrent());
        var defs = schemaDocument.RootElement.GetProperty("$defs");
        var ruleProperties = defs
            .GetProperty("objectLabelRule")
            .GetProperty("properties");

        foreach (var propertyName in new[]
                 {
                     "signature",
                     "symbolNamePattern",
                     "labelPattern",
                     "layerPattern",
                     "detectedTagPattern",
                     "sourceFormat",
                     "matchCategory",
                     "matchKind",
                     "category",
                     "kind",
                     "label",
                     "symbolName",
                     "requiresReview",
                     "confidence",
                     "evidence"
                 })
        {
            Assert.True(ruleProperties.TryGetProperty(propertyName, out _), $"Missing object label rule property '{propertyName}'.");
        }

        var ruleAllOf = defs
            .GetProperty("objectLabelRule")
            .GetProperty("allOf")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, ruleAllOf.Length);
        Assert.All(ruleAllOf, clause => Assert.True(clause.TryGetProperty("anyOf", out _)));

        var categoryEnum = defs
            .GetProperty("objectCategory")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var category in Enum.GetNames<ObjectCategory>())
        {
            Assert.Contains(category, categoryEnum);
        }

        var kindEnum = defs
            .GetProperty("objectCandidateKind")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        foreach (var kind in Enum.GetNames<ObjectCandidateKind>())
        {
            Assert.Contains(kind, kindEnum);
        }
    }

    private static async Task<PlanScanResult> CreateScanResultAsync()
    {
        var document = new PlanDocument(
            "schema-contract-test",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(520, 420),
                    new PlanPrimitive[]
                    {
                        WallLine("schema-wall-top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                        WallLine("schema-wall-right", new PlanPoint(320, 100), new PlanPoint(320, 270)),
                        WallLine("schema-wall-bottom", new PlanPoint(320, 270), new PlanPoint(100, 270)),
                        WallLine("schema-wall-left", new PlanPoint(100, 270), new PlanPoint(100, 100)),
                        new TextPrimitive("OFFICE", new PlanRect(160, 160, 45, 14)) { SourceId = "schema-room-label", Layer = "A-ROOM" },
                        new TextPrimitive("SCALE: 1:100", new PlanRect(350, 315, 90, 12)) { SourceId = "schema-scale", Layer = "A-ANNO" }
                    })
            });

        return await new OpenPlanTraceScanner().ScanAsync(document);
    }

    private static ObjectReviewDataset CreateObjectReviewDataset()
    {
        var nearbyText = new[]
        {
            new ObjectReviewTextEvidence(
                "P-101",
                1,
                new PlanRect(180, 112, 36, 12),
                "text-1",
                10.5)
        };
        var candidate = new ObjectReviewCandidate(
            "candidate-1",
            "group-1",
            1,
            ObjectCandidateKind.Symbol,
            ObjectCategory.GenericSymbol,
            ObjectCandidateSourceKind.CadSymbol,
            null,
            null,
            new PlanRect(150, 100, 24, 24),
            new PlanRect(138, 88, 48, 48),
            0.58,
            null,
            "ISO_TAG_71",
            "P-101",
            "text-1",
            "room-1",
            "PUMP ROOM",
            new[] { "symbol-1" },
            new[] { "X-SYMBOLS" },
            nearbyText,
            new[] { "candidate evidence" });
        var suggestedRule = new ObjectReviewRuleSuggestion(
            "symbol:iso_tag_71|category:GenericSymbol|kind:Symbol|layers:x-symbols",
            null,
            null,
            null,
            null,
            null,
            null,
            ObjectCategory.GenericSymbol,
            ObjectCandidateKind.Symbol,
            null,
            "ISO_TAG_71",
            true,
            0.58,
            new[] { "suggested rule evidence" });
        var group = new ObjectReviewGroup(
            "group-1",
            "symbol:iso_tag_71|category:GenericSymbol|kind:Symbol|layers:x-symbols",
            ObjectCandidateKind.Symbol,
            ObjectCategory.GenericSymbol,
            1,
            new PlanRect(150, 100, 24, 24),
            new PlanRect(138, 88, 48, 48),
            new[] { 1 },
            new[] { candidate.CandidateId },
            new[] { "symbol-1" },
            new[] { "X-SYMBOLS" },
            true,
            0.58,
            null,
            "ISO_TAG_71",
            new[] { "P-101" },
            suggestedRule,
            new[] { candidate },
            nearbyText,
            new[] { "group evidence" });

        return new ObjectReviewDataset(
            ObjectReviewDataset.CurrentSchemaVersion,
            "Review",
            "draft",
            DateTimeOffset.UtcNow,
            "doc-1",
            null,
            null,
            new[] { group },
            Array.Empty<ObjectReviewCandidate>());
    }

    private static ObjectCorrectionDataset CreateObjectCorrectionDataset() =>
        new(
            ObjectCorrectionDataset.CurrentSchemaVersion,
            "Corrections",
            "draft",
            DateTimeOffset.UtcNow,
            ObjectReviewDataset.CurrentSchemaVersion,
            "doc-1",
            "doc.dxf",
            null,
            new[]
            {
                new ObjectCorrectionAction(
                    "group-1",
                    ObjectCorrectionTargetKind.Group,
                    ObjectCorrectionDecision.Corrected,
                    ObjectCorrectionApplyScope.MatchingSignature,
                    "group-1",
                    null,
                    "symbol:iso_tag_71|category:GenericSymbol|kind:Symbol|layers:x-symbols",
                    ObjectCandidateKind.Symbol,
                    ObjectCategory.GenericSymbol,
                    null,
                    "ISO_TAG_71",
                    ObjectCandidateKind.Symbol,
                    ObjectCategory.Equipment,
                    "Isolation valve",
                    "VALVE_ISO",
                    false,
                    0.91,
                    new PlanRect(138, 88, 48, 48),
                    new[] { "P-101" },
                    new[] { 1 },
                    new[] { "candidate-1" },
                    new[] { "symbol-1" },
                    new[] { "X-SYMBOLS" },
                    new[]
                    {
                        new ObjectReviewTextEvidence(
                            "P-101",
                            1,
                            new PlanRect(180, 112, 36, 12),
                            "text-1",
                            10.5)
                    },
                    "local-user",
                    DateTimeOffset.UtcNow,
                    new[] { "User confirmed repeated symbol family." })
            });

    private static LayerCategoryProfile CreateLayerCategoryProfile() =>
        new(
            LayerCategoryProfile.CurrentSchemaVersion,
            "Industrial CAD layers",
            "1.0",
            new[]
            {
                new LayerCategoryOverride("A-WALL-*", LayerCategory.Wall, "dxf"),
                new LayerCategoryOverride("E-EQP-*", LayerCategory.Electrical)
            });

    private static ObjectLabelProfile CreateObjectLabelProfile() =>
        new(
            ObjectLabelProfile.CurrentSchemaVersion,
            "Industrial symbols",
            "1.0",
            new[]
            {
                new ObjectLabelRule
                {
                    Signature = "symbol:iso_tag_71|category:GenericSymbol|kind:Symbol|layers:x-symbols",
                    Category = ObjectCategory.Equipment,
                    Kind = ObjectCandidateKind.Symbol,
                    Label = "Isolation valve",
                    SymbolName = "VALVE_ISO",
                    RequiresReview = false,
                    Confidence = new Confidence(0.91),
                    Evidence = new[] { "User confirmed repeated symbol." }
                }
            });

    private static BenchmarkManifest CreateBenchmarkManifest() =>
        new()
        {
            Name = "Schema benchmark",
            Fixtures = new[]
            {
                new BenchmarkFixture
                {
                    Id = "case-1",
                    SourcePath = "sample.dxf",
                    Expectations = new BenchmarkExpectations
                    {
                        MinPages = 1,
                        MaxDiagnosticErrors = 0,
                        MinQualityGrade = PlanScanQualityGrade.Usable,
                        MinQualityConfidence = 0.65,
                        MaxQualityIssues = 1,
                        MinImportReadinessGrade = PlanImportReadinessGrade.ReviewRequired,
                        MinImportReadinessScore = 0.7,
                        RequireGeometryImportReady = true,
                        RequireMetricImportReady = true,
                        RequireRoutingImportReady = true,
                        AllowImportReview = true,
                        RequiredImportIssueCodes = new[] { "placement.import.measurement_outliers" },
                        ForbiddenImportIssueCodes = new[] { "placement.import.no_walls" },
                        RequiredOpeningTypes = new[] { OpeningType.Door },
                        StageExpectations = new[]
                        {
                            new BenchmarkStageExpectation
                            {
                                Stage = "openings",
                                MaxDurationMilliseconds = 100,
                                MaxErrors = 0
                            }
                        },
                        OpeningMetrics = new BenchmarkDetectorMetricExpectations
                        {
                            MinRecall = 1,
                            Targets = new[]
                            {
                                new BenchmarkDetectionTarget
                                {
                                    Id = "door-1",
                                    PageNumber = 1,
                                    OpeningType = OpeningType.Door,
                                    OpeningOperation = OpeningOperation.Hinged
                                }
                            }
                        }
                    }
                }
            }
        };

    private static BenchmarkRunResult CreateBenchmarkRunResult() =>
        BenchmarkRunResult.Create(
            "Schema benchmark run",
            new[]
            {
                new BenchmarkCaseResult(
                    "case-1",
                    "Case 1",
                    "sample.dxf",
                    true,
                    true,
                    125,
                    new BenchmarkCounts(
                        Pages: 1,
                        Regions: 2,
                        Dimensions: 1,
                        Annotations: 1,
                        AnnotationReferences: 1,
                        GridAxes: 2,
                        GridBaySpacings: 1,
                        SurfacePatterns: 0,
                        Walls: 4,
                        WallNodes: 4,
                        WallEdges: 4,
                        Rooms: 1,
                        RoomAdjacencies: 0,
                        RoomClusters: 1,
                        Openings: 1,
                        Objects: 1,
                        ObjectGroups: 1,
                        ObjectAggregates: 1,
                        RoutingItems: 2,
                        RoutingSuppressedObjects: 1,
                        Diagnostics: 0,
                        DiagnosticWarnings: 0,
                        DiagnosticErrors: 0,
                        QualityGrade: PlanScanQualityGrade.Usable,
                        QualityConfidence: 0.82,
                        QualityRequiresReview: false,
                        QualityIssues: 0,
                        HasReliableCalibration: true,
                        MeasurementCheckedCount: 1,
                        MeasurementConsistentCount: 1,
                        MeasurementOutlierCount: 0,
                        MeasurementSelectedMillimetersPerDrawingUnit: 50,
                        MeasurementMedianMillimetersPerDrawingUnit: 50,
                        MeasurementScaleSpreadRatio: 1.0,
                        MeasurementConsistencyConfidence: 0.9),
                    new[]
                    {
                        new BenchmarkAssertionResult(
                            "scan.completed",
                            true,
                            "scan succeeds",
                            "scan succeeded",
                            "Input was scanned successfully.")
                    },
                    null)
                {
                    Metrics = new[]
                    {
                        CreateBenchmarkMetric("roomMetrics", expected: 1, detected: 1, matched: 1)
                    },
                    Properties = new Dictionary<string, string>
                    {
                        ["difficulty"] = "smoke"
                    },
                    ImportReadiness = new PlanImportReadiness(
                        "Strong",
                        0.96,
                        ReadyForGeometryImport: true,
                        ReadyForMetricImport: true,
                        ReadyForRoutingImport: true,
                        RequiresReview: false,
                        BlockingIssueCodes: Array.Empty<string>(),
                        ReviewIssueCodes: Array.Empty<string>(),
                        RecommendedActions: new[] { "Schema sample is import-ready." },
                        Evidence: new[] { "schema sample import readiness" })
                }
            });

    private static BenchmarkDetectorMetrics CreateBenchmarkMetric(
        string detector,
        int expected,
        int detected,
        int matched)
    {
        var missed = Math.Max(0, expected - matched);
        var extra = Math.Max(0, detected - matched);
        var recall = expected == 0 ? 1.0 : matched / (double)expected;
        var precision = detected == 0 ? expected == 0 ? 1.0 : 0.0 : matched / (double)detected;
        var f1 = precision + recall <= 0 ? 0 : (2 * precision * recall) / (precision + recall);
        var matches = Enumerable.Range(0, expected)
            .Select(index => new BenchmarkTargetMatchResult(
                index,
                $"target-{index + 1}",
                index < matched,
                index < matched ? $"detection-{index + 1}" : null,
                index < matched ? 1 : 0,
                index < matched ? "schema match" : "schema miss"))
            .ToArray();

        return new BenchmarkDetectorMetrics(
            detector,
            expected,
            detected,
            matched,
            missed,
            extra,
            recall,
            precision,
            f1,
            matches);
    }

    private static BenchmarkComparisonResult CreateBenchmarkComparison() =>
        new(
            BenchmarkComparisonResult.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            "Baseline",
            "Candidate",
            1,
            1,
            1,
            0,
            0,
            1,
            0,
            new[]
            {
                new BenchmarkCaseComparison(
                    "case-1",
                    BenchmarkComparisonCaseStatus.Matched,
                    "Case 1",
                    "Case 1",
                    true,
                    false,
                    100,
                    150,
                    50,
                    PlanScanQualityGrade.Strong,
                    PlanScanQualityGrade.Usable,
                    0.92,
                    0.74,
                    10,
                    8,
                    0,
                    2,
                    new[]
                    {
                        new BenchmarkCountDelta("walls", 10, 8, -2)
                    },
                    new[]
                    {
                        new BenchmarkComparisonSignal(
                            "case-1",
                            "case.failed",
                            BenchmarkComparisonSignalSeverity.Regression,
                            "Candidate failed.",
                            "passed",
                            "failed")
                    })
            },
            new[]
            {
                new BenchmarkComparisonSignal(
                    "case-1",
                    "case.failed",
                    BenchmarkComparisonSignalSeverity.Regression,
                    "Candidate failed.",
                    "passed",
                    "failed")
            });

    private static BatchScanComparisonResult CreateBatchComparison() =>
        new(
            BatchScanComparisonResult.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            "baseline",
            "candidate",
            1,
            1,
            1,
            0,
            0,
            0,
            0,
            0,
            1,
            0,
            0,
            0,
            0,
            -0.006,
            -125,
            new[]
            {
                new BatchScanItemComparison(
                    "case-1.pdf",
                    BatchScanComparisonItemStatus.Matched,
                    "baseline/case-1.pdf",
                    "candidate/case-1.pdf",
                    "case-1.pdf",
                    "case-1.pdf",
                    "baseline/case-1/scan.json",
                    "candidate/case-1/scan.json",
                    "baseline/case-1/visual-snapshot.json",
                    "candidate/case-1/visual-snapshot.json",
                    "baseline/case-1/scan.geojson",
                    "candidate/case-1/scan.geojson",
                    "baseline/case-1/placement.json",
                    "candidate/case-1/placement.json",
                    "baseline/case-1/overlays",
                    "candidate/case-1/overlays",
                    BatchScanItemStatus.Succeeded,
                    BatchScanItemStatus.Succeeded,
                    1200,
                    1075,
                    -125,
                    "Usable",
                    "Usable",
                    0.804,
                    0.798,
                    0,
                    0,
                    1,
                    1,
                    0,
                    0,
                    new[]
                    {
                        new BatchScanMetricDelta("walls", 106, 39, -67, "count")
                    },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    new[]
                    {
                        new BatchScanComparisonSignal(
                            "case-1.pdf",
                            "counts.walls_changed",
                            BatchScanComparisonSignalSeverity.Info,
                            "Candidate walls count changed significantly.",
                            "106",
                            "39")
                    })
            },
            new[]
            {
                new BatchScanComparisonSignal(
                    "case-1.pdf",
                    "counts.walls_changed",
                    BatchScanComparisonSignalSeverity.Info,
                    "Candidate walls count changed significantly.",
                    "106",
                    "39")
            });

    private static BatchScanManifest CreateBatchManifest() =>
        new()
        {
            Name = "Schema batch",
            OutputDirectory = "batch-output",
            SummaryJsonPath = "batch-output/batch.json",
            Inputs = new[] { "sample.pdf", "cad" },
            Recursive = true,
            WriteSvg = true,
            WriteGeoJson = true,
            MaxDegreeOfParallelism = 2,
            RetryCount = 1,
            LayerProfiles = new[] { "layers.json" },
            LayerCategoryOverrides = new[]
            {
                new LayerCategoryOverride("A-WALL-*", LayerCategory.Wall, "dxf")
            },
            ObjectLabelProfiles = new[] { "objects.json" },
            ScannerOptions = new BatchScannerOptions
            {
                SheetMargin = 12,
                MinWallLength = 24,
                MinWallFragmentLength = 4,
                MaxWallFragmentGap = 6,
                MaxWallCandidateSeedsPerPage = 15000,
                WallMergeTolerance = 2.5,
                WallSnapTolerance = 3,
                WallThickness = 4,
                MinOpeningGap = 8,
                MaxOpeningGap = 70,
                ObjectNearbyTextSearchRadius = 90,
                MaxNearbyTextPerObject = 5
            }
        };

    private static void AssertDefinitionRequires(JsonDocument schemaDocument, string definitionName, params string[] requiredNames)
    {
        var definition = schemaDocument.RootElement
            .GetProperty("$defs")
            .GetProperty(definitionName);
        var required = ReadStringArray(definition.GetProperty("required"));

        foreach (var requiredName in requiredNames)
        {
            Assert.Contains(requiredName, required);
        }
    }

    private static string[] ReadStringArray(JsonElement element) =>
        element
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();

    private static LinePrimitive WallLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-WALL",
            Source = new PrimitiveSourceMetadata
            {
                SourceFormat = "test",
                SourceId = sourceId,
                EntityType = "LINE",
                Layer = "A-WALL",
                LineWeight = 1.0,
                DrawingSpace = SourceDrawingSpace.Model
            }
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

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static JsonSerializerOptions CreateBenchmarkJsonOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return options;
    }

    private static JsonSerializerOptions CreateProfileJsonOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return options;
    }

    private static string ToCamelCase(string value) =>
        string.IsNullOrEmpty(value)
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];
}
