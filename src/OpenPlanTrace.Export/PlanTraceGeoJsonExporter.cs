using System.Text.Json;

namespace OpenPlanTrace.Export;

public sealed record PlanTraceGeoJsonExportOptions
{
    public bool WriteIndented { get; init; } = true;
}

public static class PlanTraceGeoJsonExporter
{
    public const string CurrentSchemaVersion = "openplantrace.geojson.v1";

    public static string Serialize(
        PlanScanResult result,
        PlanTraceGeoJsonExportOptions? options = null)
    {
        var collection = CreateFeatureCollection(result);
        return JsonSerializer.Serialize(collection, CreateJsonOptions(options));
    }

    public static async ValueTask WriteAsync(
        PlanScanResult result,
        Stream stream,
        PlanTraceGeoJsonExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var collection = CreateFeatureCollection(result);
        await JsonSerializer.SerializeAsync(
                stream,
                collection,
                CreateJsonOptions(options),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static Dictionary<string, object?> CreateFeatureCollection(PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var primitiveSources = PrimitiveSourceExport.From(result.Document).ToArray();
        var sourceLookup = primitiveSources
            .Where(source => !string.IsNullOrWhiteSpace(source.SourceId))
            .ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        var wallComponentLookup = BuildWallComponentLookup(result.WallGraph.Components);
        var wallEvidenceAssessments = WallEvidenceExportHelpers.BuildAssessmentLookup(result.WallEvidenceMap);
        var features = new List<Dictionary<string, object?>>();

        features.AddRange(result.Document.Pages.Select(PageFeature));
        features.AddRange(result.SheetRegions.Select(region => RegionFeature(region, sourceLookup)));
        features.AddRange(result.TitleBlocks.Select(titleBlock => TitleBlockFeature(titleBlock, sourceLookup)));
        features.AddRange(result.GridAxes.Select(axis => GridAxisFeature(axis, sourceLookup)));
        features.AddRange(result.GridBaySpacings.Select(bay => GridBaySpacingFeature(bay, sourceLookup)));
        features.AddRange(result.Dimensions.Select(dimension => DimensionFeature(dimension, sourceLookup)));
        features.AddRange(result.Annotations.Select(annotation => AnnotationFeature(annotation, sourceLookup)));
        features.AddRange(result.Annotations.SelectMany(annotation => AnnotationReferenceFeatures(annotation, sourceLookup)));
        features.AddRange(result.SurfacePatterns.Select(pattern => SurfacePatternFeature(pattern, sourceLookup)));
        features.AddRange(result.Walls.Select(wall => WallFeature(
            wall,
            sourceLookup,
            wallComponentLookup,
            wallEvidenceAssessments.TryGetValue(wall.Id, out var assessment) ? assessment : null)));
        features.AddRange(result.WallGraph.Nodes.Select(WallNodeFeature));
        features.AddRange(result.WallGraph.Components.Select(component => WallGraphComponentFeature(component, sourceLookup)));
        features.AddRange(result.WallGraph.RepairCandidates.Select(candidate => WallGraphRepairCandidateFeature(candidate, sourceLookup)));
        features.AddRange(result.Rooms.Select(RoomFeature));
        features.AddRange(result.RoomAdjacencyGraph.Edges.Select(RoomAdjacencyFeature));
        features.AddRange(result.RoomAdjacencyGraph.Clusters.Select(RoomClusterFeature));
        features.AddRange(result.Openings.Select(opening => OpeningFeature(opening, sourceLookup)));
        features.AddRange(result.ObjectCandidates.Select(candidate => ObjectFeature(candidate, sourceLookup)));
        features.AddRange(result.ObjectGroups.Select(group => ObjectGroupFeature(group, sourceLookup)));
        features.AddRange(result.ObjectAggregates.Select(aggregate => ObjectAggregateFeature(aggregate, sourceLookup)));
        features.AddRange(result.RoutingLayer.Barriers.Select(barrier => RoutingBarrierFeature(barrier, sourceLookup)));
        features.AddRange(result.RoutingLayer.Passages.Select(passage => RoutingPassageFeature(passage, sourceLookup)));
        features.AddRange(result.RoutingLayer.Obstacles.Select(obstacle => RoutingObstacleFeature(obstacle, sourceLookup)));
        features.AddRange(result.RoutingLayer.RoomUseHints.Select(hint => RoutingRoomUseHintFeature(hint, sourceLookup)));
        features.AddRange(result.RoutingLayer.SuppressedObjects.Select(item => RoutingSuppressedObjectFeature(item, sourceLookup)));
        features.AddRange(result.RoutingLayer.IgnoredObjects.Select(item => RoutingIgnoredObjectFeature(item, sourceLookup)));

        return new Dictionary<string, object?>
        {
            ["type"] = "FeatureCollection",
            ["schemaVersion"] = CurrentSchemaVersion,
            ["coordinateSpace"] = "OpenPlanTracePageCoordinates",
            ["coordinateNote"] = "Coordinates are OpenPlanTrace page drawing units, not WGS84 longitude/latitude.",
            ["document"] = new Dictionary<string, object?>
            {
                ["id"] = result.Document.Id,
                ["sourceName"] = result.Document.Metadata.SourceName,
                ["sourcePath"] = result.Document.Metadata.SourcePath
            },
            ["features"] = features
        };
    }

    private static Dictionary<string, object?> PageFeature(PlanPage page) =>
        Feature(
            $"page:{page.Number}",
            RectGeometry(new PlanRect(0, 0, page.Size.Width, page.Size.Height)),
            Properties("page", page.Number, null)
                .AddValue("width", page.Size.Width)
                .AddValue("height", page.Size.Height)
                .AddValue("primitiveCount", page.Primitives.Count));

    private static Dictionary<string, object?> RegionFeature(
        SheetRegion region,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            $"region:{region.Id}",
            RectGeometry(region.Bounds),
            Properties("region", region.PageNumber, region.Confidence)
                .AddValue("openPlanTraceId", region.Id)
                .AddValue("regionKind", region.Kind.ToString())
                .AddValue("label", region.Label)
                .AddSource(region.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> TitleBlockFeature(
        TitleBlockAnalysis titleBlock,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            $"title-block:{titleBlock.RegionId}",
            RectGeometry(titleBlock.Bounds),
            Properties("titleBlock", titleBlock.PageNumber, titleBlock.Confidence)
                .AddValue("openPlanTraceId", titleBlock.RegionId)
                .AddValue("projectName", titleBlock.ProjectName)
                .AddValue("sheetNumber", titleBlock.SheetNumber)
                .AddValue("sheetTitle", titleBlock.SheetTitle)
                .AddValue("revision", titleBlock.Revision)
                .AddValue("issueDate", titleBlock.IssueDate)
                .AddValue("scale", titleBlock.Scale)
                .AddValue("fieldCount", titleBlock.Fields.Count)
                .AddSource(titleBlock.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> GridAxisFeature(
        GridAxis axis,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            $"grid-axis:{axis.Id}",
            LineGeometry(axis.Line),
            Properties("gridAxis", axis.PageNumber, axis.Confidence)
                .AddValue("openPlanTraceId", axis.Id)
                .AddValue("orientation", axis.Orientation.ToString())
                .AddValue("label", axis.Label)
                .AddValue("coordinate", axis.Coordinate)
                .AddValue("sourceRegionId", axis.SourceRegionId)
                .AddValue("labelSourcePrimitiveIds", axis.LabelSourcePrimitiveIds)
                .AddValue("evidence", axis.Evidence)
                .AddSource(axis.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> GridBaySpacingFeature(
        GridBaySpacing bay,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            $"grid-bay-spacing:{bay.Id}",
            LineGeometry(bay.Line),
            Properties("gridBaySpacing", bay.PageNumber, bay.Confidence)
                .AddValue("openPlanTraceId", bay.Id)
                .AddValue("axisOrientation", bay.AxisOrientation.ToString())
                .AddValue("firstAxisId", bay.FirstAxisId)
                .AddValue("firstAxisLabel", bay.FirstAxisLabel)
                .AddValue("secondAxisId", bay.SecondAxisId)
                .AddValue("secondAxisLabel", bay.SecondAxisLabel)
                .AddValue("drawingDistance", bay.DrawingDistance)
                .AddValue("distanceMeters", bay.DistanceMeters)
                .AddValue("measurementScaleGroupId", bay.MeasurementScaleGroupId)
                .AddValue("sourceRegionId", bay.SourceRegionId)
                .AddValue("evidence", bay.Evidence)
                .AddSource(bay.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> DimensionFeature(
        DimensionAnnotation dimension,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            $"dimension:{dimension.Id}",
            dimension.DimensionLine is null
                ? RectGeometry(dimension.Bounds)
                : LineGeometry(dimension.DimensionLine.Value),
            Properties("dimension", dimension.PageNumber, dimension.Confidence)
                .AddValue("openPlanTraceId", dimension.Id)
                .AddValue("dimensionKind", dimension.Kind.ToString())
                .AddValue("orientation", dimension.Orientation.ToString())
                .AddValue("text", dimension.Text)
                .AddValue("normalizedText", dimension.NormalizedText)
                .AddValue("unit", dimension.Unit.ToString())
                .AddValue("measuredMillimeters", dimension.MeasuredMillimeters)
                .AddValue("drawingLength", dimension.DrawingLength)
                .AddValue("millimetersPerDrawingUnit", dimension.MillimetersPerDrawingUnit)
                .AddValue("sourceRegionId", dimension.SourceRegionId)
                .AddValue("evidence", dimension.Evidence)
                .AddSource(dimension.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> AnnotationFeature(
        PlanAnnotationBlock annotation,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            $"annotation:{annotation.Id}",
            RectGeometry(annotation.Bounds),
            Properties("annotation", annotation.PageNumber, annotation.Confidence)
                .AddValue("openPlanTraceId", annotation.Id)
                .AddValue("annotationKind", annotation.Kind.ToString())
                .AddValue("label", annotation.Label)
                .AddValue("sourceRegionId", annotation.SourceRegionId)
                .AddValue("itemCount", annotation.Items.Count)
                .AddValue("referenceCount", annotation.Items.Sum(item => item.References.Count))
                .AddValue("evidence", annotation.Evidence)
                .AddSource(annotation.SourcePrimitiveIds, sourceLookup));

    private static IEnumerable<Dictionary<string, object?>> AnnotationReferenceFeatures(
        PlanAnnotationBlock annotation,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        foreach (var item in annotation.Items)
        {
            foreach (var reference in item.References)
            {
                yield return Feature(
                    $"annotation-reference:{reference.Id}",
                    RectGeometry(reference.Bounds),
                    Properties("annotationReference", item.PageNumber, reference.Confidence)
                        .AddValue("openPlanTraceId", reference.Id)
                        .AddValue("annotationId", annotation.Id)
                        .AddValue("annotationKind", annotation.Kind.ToString())
                        .AddValue("annotationItemId", item.Id)
                        .AddValue("annotationItemKind", item.Kind.ToString())
                        .AddValue("itemText", item.Text)
                        .AddValue("marker", reference.Marker)
                        .AddValue("text", reference.Text)
                        .AddValue("evidence", reference.Evidence)
                        .AddSource(reference.SourcePrimitiveIds, sourceLookup));
            }
        }
    }

    private static Dictionary<string, object?> WallFeature(
        WallSegment wall,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup,
        IReadOnlyDictionary<string, WallGraphComponent> wallComponentLookup,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        wallComponentLookup.TryGetValue(wall.Id, out var component);
        return
        Feature(
            $"wall:{wall.Id}",
            LineGeometry(wall.CenterLine),
            Properties("wall", wall.PageNumber, wall.Confidence)
                .AddValue("openPlanTraceId", wall.Id)
                .AddValue("detectionKind", wall.DetectionKind.ToString())
                .AddValue("wallType", wall.WallType.ToString())
                .AddValue("wallComponentId", component?.Id)
                .AddValue("wallComponentKind", component?.Kind.ToString())
                .AddValue(
                    "excludedFromStructuralTopology",
                    WallEvidenceExportHelpers.IsExcludedFromStructuralTopology(component, evidenceAssessment))
                .AddValue("thickness", wall.Thickness)
                .AddValue("drawingLength", wall.DrawingLength)
                .AddValue("lengthMeters", wall.LengthMeters)
                .AddValue("thicknessMillimeters", wall.ThicknessMillimeters)
                .AddValue("measurementScaleGroupId", wall.MeasurementScaleGroupId)
                .AddValue("pairFaceSeparation", wall.PairEvidence?.FaceSeparation)
                .AddValue("pairOverlapRatio", wall.PairEvidence?.OverlapRatio)
                .AddValue("pairScore", wall.PairEvidence?.Score)
                .AddValue("fragmentCount", wall.FragmentEvidence?.FragmentCount)
                .AddValue("fragmentGapRatio", wall.FragmentEvidence?.GapRatio)
                .AddValue("fragmentGeometryRequiresReview", wall.FragmentEvidence?.RequiresGeometryReview)
                .AddValue("wallEvidenceCategory", evidenceAssessment?.Category.ToString())
                .AddValue("wallEvidenceConfidence", evidenceAssessment?.Confidence.Value)
                .AddValue("wallEvidencePlacementReady", evidenceAssessment?.PlacementReady)
                .AddValue("wallEvidenceRequiresReview", evidenceAssessment?.RequiresReview)
                .AddValue("wallEvidenceRejectedAsNoise", evidenceAssessment?.RejectedAsNoise)
                .AddValue("wallEvidence", evidenceAssessment?.Evidence)
                .AddValue("sourceRegionId", wall.SourceRegionId)
                .AddValue("evidence", wall.Evidence)
                .AddSource(wall.SourcePrimitiveIds, sourceLookup));
    }

    private static Dictionary<string, object?> SurfacePatternFeature(
        SurfacePatternCandidate pattern,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            $"surface-pattern:{pattern.Id}",
            RectGeometry(pattern.Bounds),
            Properties("surfacePattern", pattern.PageNumber, pattern.Confidence)
                .AddValue("openPlanTraceId", pattern.Id)
                .AddValue("patternKind", pattern.Kind.ToString())
                .AddValue("orientation", pattern.Orientation.ToString())
                .AddValue("sourceRegionId", pattern.SourceRegionId)
                .AddValue("lineCount", pattern.LineCount)
                .AddValue("horizontalLineCount", pattern.HorizontalLineCount)
                .AddValue("verticalLineCount", pattern.VerticalLineCount)
                .AddValue("intersectionCount", pattern.IntersectionCount)
                .AddValue("horizontalMedianSpacing", pattern.HorizontalMedianSpacing)
                .AddValue("verticalMedianSpacing", pattern.VerticalMedianSpacing)
                .AddValue("medianSpacing", pattern.MedianSpacing)
                .AddValue("excludedFromWallDetection", pattern.ExcludedFromWallDetection)
                .AddValue("excludedFromStructuralTopology", pattern.ExcludedFromStructuralTopology)
                .AddValue("requiresReview", pattern.RequiresReview)
                .AddValue("evidence", pattern.Evidence)
                .AddSource(pattern.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> WallNodeFeature(WallNode node) =>
        Feature(
            $"wall-node:{node.Id}",
            PointGeometry(node.Position),
            Properties("wallNode", node.PageNumber, node.Confidence)
                .AddValue("openPlanTraceId", node.Id)
                .AddValue("nodeKind", node.Kind.ToString())
                .AddValue("degree", node.Degree)
                .AddValue("directions", node.Directions)
                .AddValue("evidence", node.Evidence));

    private static Dictionary<string, object?> WallGraphComponentFeature(
        WallGraphComponent component,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            $"wall-component:{component.Id}",
            RectGeometry(component.Bounds),
            Properties("wallGraphComponent", component.PageNumber, component.Confidence)
                .AddValue("openPlanTraceId", component.Id)
                .AddValue("componentKind", component.Kind.ToString())
                .AddValue("wallCount", component.WallCount)
                .AddValue("nodeCount", component.NodeCount)
                .AddValue("edgeCount", component.EdgeCount)
                .AddValue("drawingLength", component.DrawingLength)
                .AddValue("excludedFromStructuralTopology", component.ExcludedFromStructuralTopology)
                .AddValue("wallIds", component.WallIds)
                .AddValue("nodeIds", component.NodeIds)
                .AddValue("edgeIds", component.EdgeIds)
                .AddValue("evidence", component.Evidence)
                .AddSource(component.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> WallGraphRepairCandidateFeature(
        WallGraphRepairCandidate candidate,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            $"wall-graph-repair:{candidate.Id}",
            LineGeometry(candidate.RepairLine),
            Properties("wallGraphRepairCandidate", candidate.PageNumber, candidate.Confidence)
                .AddValue("openPlanTraceId", candidate.Id)
                .AddValue("candidateKind", candidate.Kind.ToString())
                .AddValue("suggestedAction", candidate.SuggestedAction.ToString())
                .AddValue("sourceNodeId", candidate.SourceNodeId)
                .AddValue("targetNodeId", candidate.TargetNodeId)
                .AddValue("hostWallId", candidate.HostWallId)
                .AddValue("gapDistance", candidate.GapDistance)
                .AddValue("requiresReview", candidate.RequiresReview)
                .AddValue("wallIds", candidate.WallIds)
                .AddValue("evidence", candidate.Evidence)
                .AddSource(candidate.SourcePrimitiveIds, sourceLookup));

    private static IReadOnlyDictionary<string, WallGraphComponent> BuildWallComponentLookup(
        IReadOnlyList<WallGraphComponent> components)
    {
        var lookup = new Dictionary<string, WallGraphComponent>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            foreach (var wallId in component.WallIds)
            {
                if (!string.IsNullOrWhiteSpace(wallId))
                {
                    lookup[wallId] = component;
                }
            }
        }

        return lookup;
    }

    private static Dictionary<string, object?> RoomFeature(RoomRegion room) =>
        Feature(
            $"room:{room.Id}",
            PolygonGeometry(room.Boundary, room.Bounds),
            Properties("room", room.PageNumber, room.Confidence)
                .AddValue("openPlanTraceId", room.Id)
                .AddValue("label", room.Label)
                .AddValue("roomUseKind", room.UseKind.ToString())
                .AddValue("drawingArea", room.DrawingArea)
                .AddValue("areaSquareMeters", room.AreaSquareMeters)
                .AddValue("measurementScaleGroupId", room.MeasurementScaleGroupId)
                .AddValue("wallIds", room.WallIds)
                .AddValue("labelSourcePrimitiveIds", room.LabelSourcePrimitiveIds)
                .AddValue("evidence", room.Evidence));

    private static Dictionary<string, object?> RoomAdjacencyFeature(RoomAdjacencyEdge edge) =>
        Feature(
            $"room-adjacency:{edge.Id}",
            edge.SharedBoundary is null ? null : LineGeometry(edge.SharedBoundary.Value),
            Properties("roomAdjacency", edge.PageNumber, edge.Confidence)
                .AddValue("openPlanTraceId", edge.Id)
                .AddValue("adjacencyKind", edge.Kind.ToString())
                .AddValue("firstRoomId", edge.FirstRoomId)
                .AddValue("firstRoomLabel", edge.FirstRoomLabel)
                .AddValue("secondRoomId", edge.SecondRoomId)
                .AddValue("secondRoomLabel", edge.SecondRoomLabel)
                .AddValue("directionFromFirstToSecond", edge.DirectionFromFirstToSecond.ToString())
                .AddValue("directionFromSecondToFirst", edge.DirectionFromSecondToFirst.ToString())
                .AddValue("sharedBoundaryLength", edge.SharedBoundaryLength)
                .AddValue("sharedWallIds", edge.SharedWallIds)
                .AddValue("openingIds", edge.OpeningIds)
                .AddValue("evidence", edge.Evidence));

    private static Dictionary<string, object?> RoomClusterFeature(RoomCluster cluster) =>
        Feature(
            $"room-cluster:{cluster.Id}",
            RectGeometry(cluster.Bounds),
            Properties("roomCluster", cluster.PageNumber, cluster.Confidence)
                .AddValue("openPlanTraceId", cluster.Id)
                .AddValue("roomIds", cluster.RoomIds)
                .AddValue("roomLabels", cluster.RoomLabels)
                .AddValue("clusterKind", cluster.Kind.ToString())
                .AddValue("drawingArea", cluster.DrawingArea)
                .AddValue("areaSquareMeters", cluster.AreaSquareMeters)
                .AddValue("roomAdjacencyIds", cluster.RoomAdjacencyIds)
                .AddValue("openingIds", cluster.OpeningIds)
                .AddValue("evidence", cluster.Evidence));

    private static Dictionary<string, object?> OpeningFeature(
        OpeningCandidate opening,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            $"opening:{opening.Id}",
            LineGeometry(opening.CenterLine),
            Properties("opening", opening.PageNumber, opening.Confidence)
                .AddValue("openPlanTraceId", opening.Id)
                .AddValue("openingType", opening.Type.ToString())
                .AddValue("operation", opening.Operation.ToString())
                .AddValue("orientation", opening.Orientation.ToString())
                .AddValue("wallId", opening.WallId)
                .AddValue("adjacentWallIds", opening.AdjacentWallIds)
                .AddValue("hostWallIds", opening.HostWallIds)
                .AddValue("connectedRoomIds", opening.ConnectedRoomIds)
                .AddValue("connectedRoomLabels", opening.ConnectedRoomLabels)
                .AddValue("connectedRoomLinkCount", opening.ConnectedRoomLinks.Count)
                .AddValue("connectedRoomLinkDistances", opening.ConnectedRoomLinks.Select(link => link.DistanceToOpening).ToArray())
                .AddValue("connectedRoomLinkConfidences", opening.ConnectedRoomLinks.Select(link => link.Confidence.Value).ToArray())
                .AddValue("connectedRoomLinkSides", opening.ConnectedRoomLinks.Select(link => link.Side.ToString()).ToArray())
                .AddValue("connectedRoomLinkSignedDistances", opening.ConnectedRoomLinks.Select(link => link.SignedDistanceFromOpening).ToArray())
                .AddValue("connectedRoomLinkRoomSidePoints", opening.ConnectedRoomLinks
                    .Select(link => link.RoomSidePoint is null ? null : Coordinate(link.RoomSidePoint.Value))
                    .ToArray())
                .AddValue("connectedRoomLinkNearestBoundaryPoints", opening.ConnectedRoomLinks
                    .Select(link => link.NearestBoundaryPoint is null ? null : Coordinate(link.NearestBoundaryPoint.Value))
                    .ToArray())
                .AddValue("roomAdjacencyIds", opening.RoomAdjacencyIds)
                .AddValue("drawingWidth", opening.DrawingWidth)
                .AddValue("widthMillimeters", opening.WidthMillimeters)
                .AddValue("measurementScaleGroupId", opening.MeasurementScaleGroupId)
                .AddValue("placementHostWallId", opening.Placement?.HostWallId)
                .AddValue("placementAnchorWallIds", opening.Placement?.AnchorWallIds)
                .AddValue("placementStartPoint", opening.Placement is null ? null : Coordinate(opening.Placement.StartPoint))
                .AddValue("placementEndPoint", opening.Placement is null ? null : Coordinate(opening.Placement.EndPoint))
                .AddValue("placementReferenceLine", opening.Placement is null
                    ? null
                    : new[] { Coordinate(opening.Placement.ReferenceLine.Start), Coordinate(opening.Placement.ReferenceLine.End) })
                .AddValue("placementFootprintCorners", opening.Placement?.FootprintCorners.Select(Coordinate).ToArray())
                .AddValue("placementStartOffsetDrawingUnits", opening.Placement?.StartOffsetDrawingUnits)
                .AddValue("placementEndOffsetDrawingUnits", opening.Placement?.EndOffsetDrawingUnits)
                .AddValue("placementCenterOffsetDrawingUnits", opening.Placement?.CenterOffsetDrawingUnits)
                .AddValue("placementLengthDrawingUnits", opening.Placement?.LengthDrawingUnits)
                .AddValue("placementStartOffsetMillimeters", opening.Placement?.StartOffsetMillimeters)
                .AddValue("placementEndOffsetMillimeters", opening.Placement?.EndOffsetMillimeters)
                .AddValue("placementCenterOffsetMillimeters", opening.Placement?.CenterOffsetMillimeters)
                .AddValue("placementLengthMillimeters", opening.Placement?.LengthMillimeters)
                .AddValue("placementHostWallStartParameter", opening.Placement?.HostWallStartParameter)
                .AddValue("placementHostWallEndParameter", opening.Placement?.HostWallEndParameter)
                .AddValue("placementHostWallCenterParameter", opening.Placement?.HostWallCenterParameter)
                .AddValue("placementAlongVector", opening.Placement is null
                    ? null
                    : new[] { opening.Placement.AlongVector.X, opening.Placement.AlongVector.Y })
                .AddValue("placementNormalVector", opening.Placement is null
                    ? null
                    : new[] { opening.Placement.NormalVector.X, opening.Placement.NormalVector.Y })
                .AddValue("placementCrossWallOffsetDrawingUnits", opening.Placement?.CrossWallOffsetDrawingUnits)
                .AddValue("placementConfidence", opening.Placement?.Confidence.Value)
                .AddValue("placementEvidence", opening.Placement?.Evidence)
                .AddValue("hingeSide", opening.HingeSide.ToString())
                .AddValue("swingSide", opening.SwingSide.ToString())
                .AddValue("swingDirection", opening.SwingDirection.ToString())
                .AddValue("evidence", opening.Evidence)
                .AddSource(opening.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> ObjectFeature(
        ObjectCandidate candidate,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            $"object:{candidate.Id}",
            RectGeometry(candidate.Bounds),
            Properties("object", candidate.PageNumber, candidate.Confidence)
                .AddValue("openPlanTraceId", candidate.Id)
                .AddValue("objectKind", candidate.Kind.ToString())
                .AddValue("category", candidate.Category.ToString())
                .AddValue("sourceKind", candidate.SourceKind.ToString())
                .AddValue("sourceWallComponentId", candidate.SourceWallComponentId)
                .AddValue("sourceWallComponentKind", candidate.SourceWallComponentKind?.ToString())
                .AddValue("label", candidate.Label)
                .AddValue("symbolName", candidate.SymbolName)
                .AddValue("detectedTag", candidate.DetectedTag)
                .AddValue("detectedTagSourcePrimitiveId", candidate.DetectedTagSourcePrimitiveId)
                .AddValue("roomId", candidate.RoomId)
                .AddValue("roomLabel", candidate.RoomLabel)
                .AddValue("nearbyText", candidate.NearbyText.Select(text => text.Text).ToArray())
                .AddValue("evidence", candidate.Evidence)
                .AddSource(candidate.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> ObjectGroupFeature(
        ObjectCandidateGroup group,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            $"object-group:{group.Id}",
            RectGeometry(group.RepresentativeBounds),
            Properties("objectGroup", group.PageNumbers.FirstOrDefault(), group.Confidence)
                .AddValue("openPlanTraceId", group.Id)
                .AddValue("signature", group.Signature)
                .AddValue("objectKind", group.Kind.ToString())
                .AddValue("category", group.Category.ToString())
                .AddValue("count", group.Count)
                .AddValue("pageNumbers", group.PageNumbers)
                .AddValue("candidateIds", group.CandidateIds)
                .AddValue("requiresReview", group.RequiresReview)
                .AddValue("label", group.Label)
                .AddValue("symbolName", group.SymbolName)
                .AddValue("detectedTags", group.DetectedTags)
                .AddValue("nearbyText", group.NearbyText.Select(text => text.Text).ToArray())
                .AddValue("evidence", group.Evidence)
                .AddSource(group.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> ObjectAggregateFeature(
        ObjectAggregate aggregate,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            $"object-aggregate:{aggregate.Id}",
            RectGeometry(aggregate.Bounds),
            Properties("objectAggregate", aggregate.PageNumber, aggregate.Confidence)
                .AddValue("openPlanTraceId", aggregate.Id)
                .AddValue("objectKind", aggregate.Kind.ToString())
                .AddValue("category", aggregate.Category.ToString())
                .AddValue("childObjectCount", aggregate.ChildObjectCount)
                .AddValue("childObjectIds", aggregate.ChildObjectIds)
                .AddValue("objectGroupIds", aggregate.ObjectGroupIds)
                .AddValue("routingInfluence", aggregate.RoutingInfluence.ToString())
                .AddValue("structuralInfluence", aggregate.StructuralInfluence.ToString())
                .AddValue("suppressChildObjectsForRouting", aggregate.SuppressChildObjectsForRouting)
                .AddValue("roomUseEvidence", aggregate.RoomUseEvidence.ToString())
                .AddValue("requiresReview", aggregate.RequiresReview)
                .AddValue("label", aggregate.Label)
                .AddValue("roomId", aggregate.RoomId)
                .AddValue("roomLabel", aggregate.RoomLabel)
                .AddValue("nearbyText", aggregate.NearbyText)
                .AddValue("sourceLayers", aggregate.SourceLayers)
                .AddValue("evidence", aggregate.Evidence)
                .AddSource(aggregate.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> RoutingBarrierFeature(
        RoutingBarrier barrier,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            barrier.Id,
            LineGeometry(barrier.CenterLine),
            Properties("routingBarrier", barrier.PageNumber, barrier.Confidence)
                .AddValue("openPlanTraceId", barrier.Id)
                .AddValue("sourceId", barrier.SourceId)
                .AddValue("sourceKind", barrier.SourceKind.ToString())
                .AddValue("thickness", barrier.Thickness)
                .AddValue("drawingLength", barrier.DrawingLength)
                .AddValue("lengthMeters", barrier.LengthMeters)
                .AddValue("thicknessMillimeters", barrier.ThicknessMillimeters)
                .AddValue("measurementScaleGroupId", barrier.MeasurementScaleGroupId)
                .AddValue("wallComponentId", barrier.WallComponentId)
                .AddValue("wallComponentKind", barrier.WallComponentKind?.ToString())
                .AddValue("excludedFromStructuralTopology", barrier.ExcludedFromStructuralTopology)
                .AddValue("evidence", barrier.Evidence)
                .AddSource(barrier.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> RoutingPassageFeature(
        RoutingPassage passage,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            passage.Id,
            LineGeometry(passage.CenterLine),
            Properties("routingPassage", passage.PageNumber, passage.Confidence)
                .AddValue("openPlanTraceId", passage.Id)
                .AddValue("sourceId", passage.SourceId)
                .AddValue("sourceKind", passage.SourceKind.ToString())
                .AddValue("openingType", passage.Type.ToString())
                .AddValue("operation", passage.Operation.ToString())
                .AddValue("orientation", passage.Orientation.ToString())
                .AddValue("drawingWidth", passage.DrawingWidth)
                .AddValue("widthMillimeters", passage.WidthMillimeters)
                .AddValue("measurementScaleGroupId", passage.MeasurementScaleGroupId)
                .AddValue("hostWallIds", passage.HostWallIds)
                .AddValue("connectedRoomIds", passage.ConnectedRoomIds)
                .AddValue("connectedRoomLabels", passage.ConnectedRoomLabels)
                .AddValue("connectedRoomLinkCount", passage.ConnectedRoomLinks.Count)
                .AddValue("connectedRoomLinkDistances", passage.ConnectedRoomLinks.Select(link => link.DistanceToOpening).ToArray())
                .AddValue("connectedRoomLinkConfidences", passage.ConnectedRoomLinks.Select(link => link.Confidence.Value).ToArray())
                .AddValue("connectedRoomLinkSides", passage.ConnectedRoomLinks.Select(link => link.Side.ToString()).ToArray())
                .AddValue("connectedRoomLinkSignedDistances", passage.ConnectedRoomLinks.Select(link => link.SignedDistanceFromOpening).ToArray())
                .AddValue("connectedRoomLinkRoomSidePoints", passage.ConnectedRoomLinks
                    .Select(link => link.RoomSidePoint is null ? null : Coordinate(link.RoomSidePoint.Value))
                    .ToArray())
                .AddValue("connectedRoomLinkNearestBoundaryPoints", passage.ConnectedRoomLinks
                    .Select(link => link.NearestBoundaryPoint is null ? null : Coordinate(link.NearestBoundaryPoint.Value))
                    .ToArray())
                .AddValue("roomAdjacencyIds", passage.RoomAdjacencyIds)
                .AddValue("placementHostWallId", passage.Placement?.HostWallId)
                .AddValue("placementAnchorWallIds", passage.Placement?.AnchorWallIds)
                .AddValue("placementStartPoint", passage.Placement is null ? null : Coordinate(passage.Placement.StartPoint))
                .AddValue("placementEndPoint", passage.Placement is null ? null : Coordinate(passage.Placement.EndPoint))
                .AddValue("placementReferenceLine", passage.Placement is null
                    ? null
                    : new[] { Coordinate(passage.Placement.ReferenceLine.Start), Coordinate(passage.Placement.ReferenceLine.End) })
                .AddValue("placementFootprintCorners", passage.Placement?.FootprintCorners.Select(Coordinate).ToArray())
                .AddValue("placementStartOffsetDrawingUnits", passage.Placement?.StartOffsetDrawingUnits)
                .AddValue("placementEndOffsetDrawingUnits", passage.Placement?.EndOffsetDrawingUnits)
                .AddValue("placementCenterOffsetDrawingUnits", passage.Placement?.CenterOffsetDrawingUnits)
                .AddValue("placementLengthDrawingUnits", passage.Placement?.LengthDrawingUnits)
                .AddValue("placementStartOffsetMillimeters", passage.Placement?.StartOffsetMillimeters)
                .AddValue("placementEndOffsetMillimeters", passage.Placement?.EndOffsetMillimeters)
                .AddValue("placementCenterOffsetMillimeters", passage.Placement?.CenterOffsetMillimeters)
                .AddValue("placementLengthMillimeters", passage.Placement?.LengthMillimeters)
                .AddValue("placementStatus", passage.Placement is null ? "Unanchored" : "Anchored")
                .AddValue("readyForCoordinatePlacement", passage.ReadyForCoordinatePlacement)
                .AddValue("requiresReview", passage.RequiresReview)
                .AddValue("reviewReasons", passage.ReviewReasons)
                .AddValue("evidence", passage.Evidence)
                .AddSource(passage.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> RoutingObstacleFeature(
        RoutingObstacle obstacle,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            obstacle.Id,
            RectGeometry(obstacle.Bounds),
            Properties("routingObstacle", obstacle.PageNumber, obstacle.Confidence)
                .AddValue("openPlanTraceId", obstacle.Id)
                .AddValue("sourceId", obstacle.SourceId)
                .AddValue("sourceKind", obstacle.SourceKind.ToString())
                .AddValue("obstacleKind", obstacle.ObstacleKind.ToString())
                .AddValue("routingInfluence", obstacle.RoutingInfluence.ToString())
                .AddValue("structuralInfluence", obstacle.StructuralInfluence.ToString())
                .AddValue("category", obstacle.Category.ToString())
                .AddValue("objectKind", obstacle.ObjectKind.ToString())
                .AddValue("label", obstacle.Label)
                .AddValue("roomId", obstacle.RoomId)
                .AddValue("roomLabel", obstacle.RoomLabel)
                .AddValue("suppressesChildObjects", obstacle.SuppressesChildObjects)
                .AddValue("childObjectIds", obstacle.ChildObjectIds)
                .AddValue("evidence", obstacle.Evidence)
                .AddSource(obstacle.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> RoutingRoomUseHintFeature(
        RoutingRoomUseHint hint,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            hint.Id,
            RectGeometry(hint.Bounds),
            Properties("routingRoomUseHint", hint.PageNumber, hint.Confidence)
                .AddValue("openPlanTraceId", hint.Id)
                .AddValue("sourceId", hint.SourceId)
                .AddValue("sourceKind", hint.SourceKind.ToString())
                .AddValue("roomUseKind", hint.RoomUseKind.ToString())
                .AddValue("roomId", hint.RoomId)
                .AddValue("roomLabel", hint.RoomLabel)
                .AddValue("evidence", hint.Evidence)
                .AddSource(hint.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> RoutingSuppressedObjectFeature(
        RoutingSuppressedObject suppressed,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            suppressed.Id,
            RectGeometry(suppressed.CandidateBounds),
            Properties("routingSuppressedObject", suppressed.PageNumber, suppressed.Confidence)
                .AddValue("openPlanTraceId", suppressed.Id)
                .AddValue("objectCandidateId", suppressed.ObjectCandidateId)
                .AddValue("suppressedByAggregateId", suppressed.SuppressedByAggregateId)
                .AddValue("reason", suppressed.Reason.ToString())
                .AddValue("action", suppressed.Action.ToString())
                .AddValue("replacementRoutingObstacleId", suppressed.ReplacementRoutingObstacleId)
                .AddValue("roomUseHintId", suppressed.RoomUseHintId)
                .AddValue("aggregateRoutingInfluence", suppressed.AggregateRoutingInfluence.ToString())
                .AddValue("aggregateStructuralInfluence", suppressed.AggregateStructuralInfluence.ToString())
                .AddValue("candidateCategory", suppressed.CandidateCategory.ToString())
                .AddValue("candidateKind", suppressed.CandidateKind.ToString())
                .AddValue("candidateLabel", suppressed.CandidateLabel)
                .AddValue("roomId", suppressed.RoomId)
                .AddValue("roomLabel", suppressed.RoomLabel)
                .AddValue("evidence", suppressed.Evidence)
                .AddSource(suppressed.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> RoutingIgnoredObjectFeature(
        RoutingIgnoredObject ignored,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        Feature(
            ignored.Id,
            RectGeometry(ignored.CandidateBounds),
            Properties("routingIgnoredObject", ignored.PageNumber, ignored.Confidence)
                .AddValue("openPlanTraceId", ignored.Id)
                .AddValue("objectCandidateId", ignored.ObjectCandidateId)
                .AddValue("reason", ignored.Reason.ToString())
                .AddValue("routingInfluence", ignored.RoutingInfluence.ToString())
                .AddValue("structuralInfluence", ignored.StructuralInfluence.ToString())
                .AddValue("candidateCategory", ignored.CandidateCategory.ToString())
                .AddValue("candidateKind", ignored.CandidateKind.ToString())
                .AddValue("candidateSourceKind", ignored.CandidateSourceKind.ToString())
                .AddValue("sourceWallComponentId", ignored.SourceWallComponentId)
                .AddValue("sourceWallComponentKind", ignored.SourceWallComponentKind?.ToString())
                .AddValue("candidateLabel", ignored.CandidateLabel)
                .AddValue("roomId", ignored.RoomId)
                .AddValue("roomLabel", ignored.RoomLabel)
                .AddValue("suppressedObjectId", ignored.SuppressedObjectId)
                .AddValue("suppressedByAggregateId", ignored.SuppressedByAggregateId)
                .AddValue("roomUseHintId", ignored.RoomUseHintId)
                .AddValue("evidence", ignored.Evidence)
                .AddSource(ignored.SourcePrimitiveIds, sourceLookup));

    private static Dictionary<string, object?> Feature(
        string id,
        Dictionary<string, object?>? geometry,
        Dictionary<string, object?> properties) =>
        new()
        {
            ["type"] = "Feature",
            ["id"] = id,
            ["geometry"] = geometry,
            ["properties"] = properties
        };

    private static Dictionary<string, object?> Properties(
        string featureType,
        int pageNumber,
        Confidence? confidence)
    {
        var properties = new Dictionary<string, object?>
        {
            ["featureType"] = featureType,
            ["pageNumber"] = pageNumber
        };

        if (confidence is not null)
        {
            properties["confidence"] = confidence.Value.Value;
        }

        return properties;
    }

    private static Dictionary<string, object?> PointGeometry(PlanPoint point) =>
        new()
        {
            ["type"] = "Point",
            ["coordinates"] = Coordinate(point)
        };

    private static Dictionary<string, object?> LineGeometry(PlanLineSegment line) =>
        new()
        {
            ["type"] = "LineString",
            ["coordinates"] = new[] { Coordinate(line.Start), Coordinate(line.End) }
        };

    private static Dictionary<string, object?> RectGeometry(PlanRect rect) =>
        PolygonGeometry(RectPoints(rect), rect);

    private static Dictionary<string, object?> PolygonGeometry(
        IReadOnlyList<PlanPoint> points,
        PlanRect fallbackBounds)
    {
        var source = points.Count >= 3 ? points : RectPoints(fallbackBounds);
        var ring = source.Select(Coordinate).ToList();
        if (ring.Count == 0)
        {
            ring.Add(Coordinate(new PlanPoint(fallbackBounds.Left, fallbackBounds.Top)));
        }

        if (!SameCoordinate(ring[0], ring[^1]))
        {
            ring.Add(ring[0]);
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "Polygon",
            ["coordinates"] = new[] { ring.ToArray() }
        };
    }

    private static IReadOnlyList<PlanPoint> RectPoints(PlanRect rect) =>
        new[]
        {
            new PlanPoint(rect.Left, rect.Top),
            new PlanPoint(rect.Right, rect.Top),
            new PlanPoint(rect.Right, rect.Bottom),
            new PlanPoint(rect.Left, rect.Bottom)
        };

    private static double[] Coordinate(PlanPoint point) =>
        new[] { point.X, point.Y };

    private static bool SameCoordinate(double[] first, double[] second) =>
        first.Length == second.Length
        && first.Zip(second).All(pair => Math.Abs(pair.First - pair.Second) <= 0.000001);

    private static Dictionary<string, object?> AddValue(
        this Dictionary<string, object?> properties,
        string key,
        object? value)
    {
        if (value is null)
        {
            return properties;
        }

        if (value is string text && string.IsNullOrWhiteSpace(text))
        {
            return properties;
        }

        properties[key] = value;
        return properties;
    }

    private static Dictionary<string, object?> AddSource(
        this Dictionary<string, object?> properties,
        IReadOnlyList<string> sourcePrimitiveIds,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        properties["sourcePrimitiveIds"] = sourcePrimitiveIds;
        properties["sourceLayers"] = ExportSourceHelpers.SourceLayers(sourcePrimitiveIds, sourceLookup);
        return properties;
    }

    private static JsonSerializerOptions CreateJsonOptions(PlanTraceGeoJsonExportOptions? options)
    {
        options ??= new PlanTraceGeoJsonExportOptions();
        return new JsonSerializerOptions
        {
            WriteIndented = options.WriteIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}
