using System.Globalization;
using System.Text.Json;

namespace OpenPlanTrace;

public static class BenchmarkManifestDraftBuilder
{
    public static BenchmarkManifest FromScanJson(
        JsonDocument scanJson,
        BenchmarkManifestDraftOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scanJson);

        options ??= new BenchmarkManifestDraftOptions();
        ValidateOptions(options);

        var root = scanJson.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Scan JSON root must be an object.", nameof(scanJson));
        }

        var document = TryGetObject(root, "document");
        var fixtureId = Clean(options.FixtureId) ?? "scan-draft";
        var sourcePath = Clean(options.SourcePath)
            ?? GetString(document, "sourcePath")
            ?? GetString(document, "sourceName")
            ?? $"{fixtureId}.plan";
        var sourceName = GetString(document, "sourceName")
            ?? GetString(document, "id")
            ?? GetString(root, "documentId");
        var fixtureName = Clean(options.FixtureName) ?? sourceName ?? fixtureId;

        var fixture = new BenchmarkFixture
        {
            Id = fixtureId,
            Name = fixtureName,
            SourcePath = sourcePath,
            Optional = options.Optional,
            SkipReason = options.Optional ? Clean(options.SkipReason) : null,
            Expectations = BuildExpectations(root, options),
            Properties = BuildProperties(root, document)
        };

        return new BenchmarkManifest
        {
            Name = Clean(options.ManifestName) ?? $"{fixtureName} benchmark draft",
            Fixtures = new[] { fixture }
        };
    }

    private static BenchmarkExpectations BuildExpectations(
        JsonElement root,
        BenchmarkManifestDraftOptions options)
    {
        var diagnostics = TryGetObject(root, "diagnostics");
        var quality = TryGetObject(root, "quality");
        var importReadiness = TryGetObject(root, "importReadiness");
        var measurementConsistency = TryGetObject(root, "measurementConsistency");
        var qualityIssues = CountArray(quality, "issues");
        var measurementCheckedCount = GetInt(measurementConsistency, "checkedCount");
        var measurementConsistentCount = GetInt(measurementConsistency, "consistentCount");
        var measurementOutlierCount = GetInt(measurementConsistency, "outlierCount");

        return new BenchmarkExpectations
        {
            MinPages = Positive(CountArray(root, "pages")),
            MinRegions = Positive(CountArray(root, "regions")),
            MinDimensions = Positive(CountArray(root, "dimensions")),
            MinAnnotations = Positive(CountArray(root, "annotations")),
            MinAnnotationReferences = Positive(CountAnnotationReferences(root)),
            MinGridAxes = Positive(CountArray(root, "gridAxes")),
            MinGridBaySpacings = Positive(CountArray(root, "gridBaySpacings")),
            MinSurfacePatterns = Positive(CountArray(root, "surfacePatterns")),
            MinWalls = Positive(CountArray(root, "walls")),
            MinWallNodes = Positive(CountArray(TryGetObject(root, "wallGraph"), "nodes")),
            MinWallEdges = Positive(CountArray(TryGetObject(root, "wallGraph"), "edges")),
            MinRooms = Positive(CountArray(root, "rooms")),
            MinRoomAdjacencies = Positive(CountArray(TryGetObject(root, "roomAdjacencyGraph"), "edges")),
            MinRoomClusters = Positive(CountArray(TryGetObject(root, "roomAdjacencyGraph"), "clusters")),
            MinOpenings = Positive(CountArray(root, "openings")),
            MinObjects = Positive(CountArray(root, "objects")),
            MinObjectGroups = Positive(CountArray(root, "objectGroups")),
            MinObjectAggregates = Positive(CountArray(root, "objectAggregates")),
            MinRoutingItems = Positive(CountRoutingItems(root)),
            MinRoutingSuppressedObjects = Positive(CountRoutingSuppressedObjects(root)),
            MaxDiagnosticWarnings = GetInt(diagnostics, "warningCount") ?? GetInt(quality, "diagnosticWarningCount"),
            MaxDiagnosticErrors = GetInt(diagnostics, "errorCount") ?? GetInt(quality, "diagnosticErrorCount"),
            MinQualityGrade = RelaxQualityGrade(ReadEnum<PlanScanQualityGrade>(quality, "grade")),
            MinQualityConfidence = ConfidenceFloor(GetDouble(quality, "overallConfidence")),
            MaxQualityIssues = qualityIssues >= 0 ? qualityIssues : null,
            MaxScanRiskIssues = PositiveOrZero(CountQualityIssues(quality, "quality.scan_risk")),
            MaxScanReviewQueueItems = PositiveOrZero(CountArray(root, "reviewQueue")),
            MaxScanReviewQueueKindCounts = CountReviewQueueKinds(root),
            MinImportReadinessGrade = RelaxImportReadinessGrade(ReadEnum<PlanImportReadinessGrade>(importReadiness, "grade")),
            MinImportReadinessScore = ConfidenceFloor(GetDouble(importReadiness, "score")),
            RequireGeometryImportReady = GetBool(importReadiness, "readyForGeometryImport") == true ? true : null,
            RequireMetricImportReady = GetBool(importReadiness, "readyForMetricImport") == true ? true : null,
            RequireRoutingImportReady = GetBool(importReadiness, "readyForRoutingImport") == true ? true : null,
            AllowImportReview = GetBool(importReadiness, "requiresReview"),
            RequiresReliableCalibration =
                GetBool(quality, "hasReliableCalibration") == true
                || GetBool(measurementConsistency, "hasReliableCalibration") == true
                    ? true
                    : null,
            MinMeasurementCheckedCount = measurementCheckedCount is > 0 ? measurementCheckedCount : null,
            MinMeasurementConsistentCount = measurementConsistentCount is > 0 ? measurementConsistentCount : null,
            MaxMeasurementOutlierCount = measurementCheckedCount is > 0 && measurementOutlierCount is >= 0 ? measurementOutlierCount : null,
            MaxMeasurementOutlierRatio = MeasurementOutlierRatio(measurementCheckedCount, measurementOutlierCount),
            MaxMeasurementScaleSpreadRatio = GetDouble(measurementConsistency, "dimensionScaleSpreadRatio") is > 0
                ? GetDouble(measurementConsistency, "dimensionScaleSpreadRatio")
                : null,
            RegionMetrics = CreateMetric(CreateRegionTargets(root, options), options),
            DimensionMetrics = CreateMetric(CreateDimensionTargets(root, options), options),
            AnnotationMetrics = CreateMetric(CreateAnnotationTargets(root, options), options),
            AnnotationReferenceMetrics = CreateMetric(CreateAnnotationReferenceTargets(root, options), options),
            GridAxisMetrics = CreateMetric(CreateGridAxisTargets(root, options), options),
            WallMetrics = CreateMetric(CreateWallTargets(root, options), options),
            RoomMetrics = CreateMetric(CreateRoomTargets(root, options), options),
            OpeningMetrics = CreateMetric(CreateOpeningTargets(root, options), options),
            ObjectMetrics = CreateMetric(CreateObjectTargets(root, options), options),
            ObjectGroupMetrics = CreateMetric(CreateObjectGroupTargets(root, options), options),
            ObjectAggregateMetrics = CreateMetric(CreateObjectAggregateTargets(root, options), options),
            RoutingBarrierMetrics = CreateMetric(CreateRoutingBarrierTargets(root, options), options),
            RoutingPassageMetrics = CreateMetric(CreateRoutingPassageTargets(root, options), options),
            RoutingObstacleMetrics = CreateMetric(CreateRoutingObstacleTargets(root, options), options),
            RoutingRoomUseHintMetrics = CreateMetric(CreateRoutingRoomUseHintTargets(root, options), options),
            RoutingSuppressedObjectMetrics = CreateMetric(CreateRoutingSuppressedObjectTargets(root, options), options),
            LayerMetrics = CreateMetric(CreateLayerTargets(root, options), options)
        };
    }

    private static IReadOnlyDictionary<string, string> BuildProperties(
        JsonElement root,
        JsonElement? document)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["draftedFrom"] = "scan-json",
            ["draftReview"] = "review generated targets before treating them as ground truth"
        };

        AddProperty(properties, "scanSchemaVersion", GetString(root, "schemaVersion"));
        AddProperty(properties, "scanGeneratedAt", GetString(root, "generatedAt"));
        AddProperty(properties, "sourceName", GetString(document, "sourceName"));
        AddProperty(properties, "sourceDocumentId", GetString(document, "id") ?? GetString(root, "documentId"));

        return properties;
    }

    private static IEnumerable<BenchmarkDetectionTarget> CreateRegionTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options) =>
        OrderedByConfidence(root, "regions")
            .Select((region, index) =>
            {
                var bounds = ReadBounds(region, "bounds", options);
                return WithProvenance(new BenchmarkDetectionTarget
                {
                    Id = TargetId(region, "region", index),
                    PageNumber = GetInt(region, "pageNumber"),
                    Bounds = bounds,
                    MinIntersectionOverUnion = bounds is null ? null : 0.5,
                    Label = Clean(GetString(region, "label")),
                    RegionKind = ReadEnum<RegionKind>(region, "kind")
                }, region);
            });

    private static IEnumerable<BenchmarkDetectionTarget> CreateDimensionTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options) =>
        OrderedByConfidence(root, "dimensions")
            .Select((dimension, index) =>
            {
                var bounds = ReadBounds(dimension, "bounds", options);
                return WithProvenance(new BenchmarkDetectionTarget
                {
                    Id = TargetId(dimension, "dimension", index),
                    PageNumber = GetInt(dimension, "pageNumber"),
                    Bounds = bounds,
                    MinIntersectionOverUnion = bounds is null ? null : 0.35,
                    Text = Clean(GetString(dimension, "normalizedText")) ?? Clean(GetString(dimension, "text")),
                    DimensionKind = ReadEnum<DimensionKind>(dimension, "kind"),
                    DimensionOrientation = ReadEnum<DimensionOrientation>(dimension, "orientation")
                }, dimension);
            });

    private static IEnumerable<BenchmarkDetectionTarget> CreateAnnotationTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options) =>
        OrderedByConfidence(root, "annotations")
            .Select((annotation, index) =>
            {
                var bounds = ReadBounds(annotation, "bounds", options);
                return WithProvenance(new BenchmarkDetectionTarget
                {
                    Id = TargetId(annotation, "annotation", index),
                    PageNumber = GetInt(annotation, "pageNumber"),
                    Bounds = bounds,
                    MinIntersectionOverUnion = bounds is null ? null : 0.4,
                    Label = Clean(GetString(annotation, "label")),
                    AnnotationKind = ReadEnum<PlanAnnotationKind>(annotation, "kind")
                }, annotation);
            });

    private static IEnumerable<BenchmarkDetectionTarget> CreateAnnotationReferenceTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options)
    {
        var index = 0;
        foreach (var annotation in EnumerateArray(root, "annotations"))
        {
            var annotationKind = ReadEnum<PlanAnnotationKind>(annotation, "kind");
            var annotationLabel = Clean(GetString(annotation, "label"));
            var annotationPage = GetInt(annotation, "pageNumber");

            foreach (var item in EnumerateArray(annotation, "items"))
            {
                var pageNumber = GetInt(item, "pageNumber") ?? annotationPage;
                var itemMarker = Clean(GetString(item, "marker"));
                foreach (var reference in EnumerateArray(item, "references"))
                {
                    var bounds = ReadBounds(reference, "bounds", options);
                    yield return WithProvenance(new BenchmarkDetectionTarget
                    {
                        Id = TargetId(reference, "annotation-reference", index++),
                        PageNumber = pageNumber,
                        Bounds = bounds,
                        MinIntersectionOverUnion = bounds is null ? null : 0.4,
                        Label = annotationLabel,
                        Text = Clean(GetString(reference, "text")),
                        Marker = Clean(GetString(reference, "marker")) ?? itemMarker,
                        AnnotationKind = annotationKind
                    }, reference);
                }
            }
        }
    }

    private static IEnumerable<BenchmarkDetectionTarget> CreateGridAxisTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options) =>
        OrderedByConfidence(root, "gridAxes")
            .Select((axis, index) =>
            {
                var bounds = ReadBounds(axis, "bounds", options);
                return WithProvenance(new BenchmarkDetectionTarget
                {
                    Id = TargetId(axis, "grid-axis", index),
                    PageNumber = GetInt(axis, "pageNumber"),
                    Bounds = bounds,
                    MinIntersectionOverUnion = bounds is null ? null : 0.4,
                    Label = Clean(GetString(axis, "label")),
                    GridAxisOrientation = ReadEnum<GridAxisOrientation>(axis, "orientation")
                }, axis);
            });

    private static IEnumerable<BenchmarkDetectionTarget> CreateWallTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options) =>
        OrderedByConfidence(root, "walls")
            .Select((wall, index) =>
            {
                var bounds = ReadBounds(wall, "bounds", options);
                return WithProvenance(new BenchmarkDetectionTarget
                {
                    Id = TargetId(wall, "wall", index),
                    PageNumber = GetInt(wall, "pageNumber"),
                    Bounds = bounds,
                    MinIntersectionOverUnion = bounds is null ? null : 0.35
                }, wall);
            });

    private static IEnumerable<BenchmarkDetectionTarget> CreateRoomTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options) =>
        OrderedByConfidence(root, "rooms")
            .Select((room, index) =>
            {
                var bounds = ReadBounds(room, "bounds", options);
                return WithProvenance(new BenchmarkDetectionTarget
                {
                    Id = TargetId(room, "room", index),
                    PageNumber = GetInt(room, "pageNumber"),
                    Bounds = bounds,
                    MinIntersectionOverUnion = bounds is null ? null : 0.5,
                    Label = Clean(GetString(room, "label"))
                }, room, "labelSourcePrimitiveIds");
            });

    private static IEnumerable<BenchmarkDetectionTarget> CreateOpeningTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options) =>
        OrderedByConfidence(root, "openings")
            .Select((opening, index) =>
            {
                var bounds = ReadBounds(opening, "bounds", options);
                return WithProvenance(new BenchmarkDetectionTarget
                {
                    Id = TargetId(opening, "opening", index),
                    PageNumber = GetInt(opening, "pageNumber"),
                    Bounds = bounds,
                    MinIntersectionOverUnion = bounds is null ? null : 0.35,
                    OpeningType = ReadEnum<OpeningType>(opening, "type"),
                    OpeningOperation = ReadEnum<OpeningOperation>(opening, "operation")
                }, opening);
            });

    private static IEnumerable<BenchmarkDetectionTarget> CreateObjectTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options) =>
        OrderedByConfidence(root, "objects")
            .Select((candidate, index) =>
            {
                var bounds = ReadBounds(candidate, "bounds", options);
                return WithProvenance(new BenchmarkDetectionTarget
                {
                    Id = TargetId(candidate, "object", index),
                    PageNumber = GetInt(candidate, "pageNumber"),
                    Bounds = bounds,
                    MinIntersectionOverUnion = bounds is null ? null : 0.35,
                    Label = Clean(GetString(candidate, "label")),
                    Text = Clean(GetString(candidate, "symbolName")),
                    ObjectCategory = ReadEnum<ObjectCategory>(candidate, "category"),
                    ObjectKind = ReadEnum<ObjectCandidateKind>(candidate, "kind"),
                    DetectedTags = ReadDetectedTags(candidate)
                }, candidate);
            });

    private static IEnumerable<BenchmarkDetectionTarget> CreateObjectGroupTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options) =>
        OrderedByConfidence(root, "objectGroups")
            .Select((group, index) =>
            {
                var bounds = ReadBounds(group, "representativeBounds", options);
                return WithProvenance(new BenchmarkDetectionTarget
                {
                    Id = TargetId(group, "object-group", index),
                    PageNumber = SinglePageNumber(group),
                    Bounds = bounds,
                    MinIntersectionOverUnion = bounds is null ? null : 0.3,
                    Label = Clean(GetString(group, "label")),
                    Text = Clean(GetString(group, "symbolName")) ?? Clean(GetString(group, "signature")),
                    ObjectCategory = ReadEnum<ObjectCategory>(group, "category"),
                    ObjectKind = ReadEnum<ObjectCandidateKind>(group, "kind"),
                    MinCount = GetInt(group, "count"),
                    RequiresReview = GetBool(group, "requiresReview"),
                    DetectedTags = ReadDetectedTags(group)
                }, group);
            });

    private static IEnumerable<BenchmarkDetectionTarget> CreateObjectAggregateTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options) =>
        OrderedByConfidence(root, "objectAggregates")
            .Select((aggregate, index) =>
            {
                var bounds = ReadBounds(aggregate, "bounds", options);
                return WithProvenance(new BenchmarkDetectionTarget
                {
                    Id = TargetId(aggregate, "object-aggregate", index),
                    PageNumber = GetInt(aggregate, "pageNumber"),
                    Bounds = bounds,
                    MinIntersectionOverUnion = bounds is null ? null : 0.35,
                    Label = Clean(GetString(aggregate, "label")),
                    ObjectCategory = ReadEnum<ObjectCategory>(aggregate, "category"),
                    ObjectKind = ReadEnum<ObjectCandidateKind>(aggregate, "kind"),
                    MinCount = GetInt(aggregate, "childObjectCount") ?? Positive(CountArray(aggregate, "childObjectIds")),
                    RequiresReview = GetBool(aggregate, "requiresReview"),
                    RoutingInfluence = ReadEnum<ObjectRoutingInfluence>(aggregate, "routingInfluence"),
                    StructuralInfluence = ReadEnum<ObjectStructuralInfluence>(aggregate, "structuralInfluence"),
                    SuppressesChildObjects = GetBool(aggregate, "suppressChildObjectsForRouting"),
                    RoomUseKind = ReadEnum<RoomUseKind>(aggregate, "roomUseEvidence")
                }, aggregate);
            });

    private static IEnumerable<BenchmarkDetectionTarget> CreateRoutingBarrierTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options)
    {
        var routingLayer = TryGetObject(root, "routingLayer");
        return OrderedByConfidence(routingLayer, "barriers")
            .Select((barrier, index) =>
            {
                var bounds = ReadBounds(barrier, "bounds", options);
                return WithProvenance(new BenchmarkDetectionTarget
                {
                    Id = TargetId(barrier, "routing-barrier", index),
                    PageNumber = GetInt(barrier, "pageNumber"),
                    Bounds = bounds,
                    MinIntersectionOverUnion = bounds is null ? null : 0.35,
                    RoutingSourceKind = ReadEnum<RoutingSourceKind>(barrier, "sourceKind")
                }, barrier);
            });
    }

    private static IEnumerable<BenchmarkDetectionTarget> CreateRoutingPassageTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options)
    {
        var routingLayer = TryGetObject(root, "routingLayer");
        return OrderedByConfidence(routingLayer, "passages")
            .Select((passage, index) =>
            {
                var bounds = ReadBounds(passage, "bounds", options);
                return WithProvenance(new BenchmarkDetectionTarget
                {
                    Id = TargetId(passage, "routing-passage", index),
                    PageNumber = GetInt(passage, "pageNumber"),
                    Bounds = bounds,
                    MinIntersectionOverUnion = bounds is null ? null : 0.35,
                    OpeningType = ReadEnum<OpeningType>(passage, "type"),
                    OpeningOperation = ReadEnum<OpeningOperation>(passage, "operation"),
                    RoutingSourceKind = ReadEnum<RoutingSourceKind>(passage, "sourceKind")
                }, passage);
            });
    }

    private static IEnumerable<BenchmarkDetectionTarget> CreateRoutingObstacleTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options)
    {
        var routingLayer = TryGetObject(root, "routingLayer");
        return OrderedByConfidence(routingLayer, "obstacles")
            .Select((obstacle, index) =>
            {
                var bounds = ReadBounds(obstacle, "bounds", options);
                return WithProvenance(new BenchmarkDetectionTarget
                {
                    Id = TargetId(obstacle, "routing-obstacle", index),
                    PageNumber = GetInt(obstacle, "pageNumber"),
                    Bounds = bounds,
                    MinIntersectionOverUnion = bounds is null ? null : 0.35,
                    Label = Clean(GetString(obstacle, "label")),
                    Text = Clean(GetString(obstacle, "roomLabel")),
                    ObjectCategory = ReadEnum<ObjectCategory>(obstacle, "category"),
                    ObjectKind = ReadEnum<ObjectCandidateKind>(obstacle, "objectKind"),
                    MinCount = Positive(CountArray(obstacle, "childObjectIds")),
                    RoutingSourceKind = ReadEnum<RoutingSourceKind>(obstacle, "sourceKind"),
                    RoutingObstacleKind = ReadEnum<RoutingObstacleKind>(obstacle, "obstacleKind"),
                    RoutingInfluence = ReadEnum<ObjectRoutingInfluence>(obstacle, "routingInfluence"),
                    StructuralInfluence = ReadEnum<ObjectStructuralInfluence>(obstacle, "structuralInfluence"),
                    SuppressesChildObjects = GetBool(obstacle, "suppressesChildObjects")
                }, obstacle);
            });
    }

    private static IEnumerable<BenchmarkDetectionTarget> CreateRoutingRoomUseHintTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options)
    {
        var routingLayer = TryGetObject(root, "routingLayer");
        return OrderedByConfidence(routingLayer, "roomUseHints")
            .Select((hint, index) =>
            {
                var bounds = ReadBounds(hint, "bounds", options);
                return WithProvenance(new BenchmarkDetectionTarget
                {
                    Id = TargetId(hint, "routing-room-use-hint", index),
                    PageNumber = GetInt(hint, "pageNumber"),
                    Bounds = bounds,
                    MinIntersectionOverUnion = bounds is null ? null : 0.35,
                    Label = Clean(GetString(hint, "roomLabel")),
                    RoutingSourceKind = ReadEnum<RoutingSourceKind>(hint, "sourceKind"),
                    RoomUseKind = ReadEnum<RoomUseKind>(hint, "roomUseKind")
                }, hint);
            });
    }

    private static IEnumerable<BenchmarkDetectionTarget> CreateRoutingSuppressedObjectTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options)
    {
        var routingLayer = TryGetObject(root, "routingLayer");
        return OrderedByConfidence(routingLayer, "suppressedObjects")
            .Select((suppressed, index) =>
            {
                var bounds = ReadBounds(suppressed, "candidateBounds", options);
                return WithProvenance(new BenchmarkDetectionTarget
                {
                    Id = TargetId(suppressed, "routing-suppressed-object", index),
                    PageNumber = GetInt(suppressed, "pageNumber"),
                    Bounds = bounds,
                    MinIntersectionOverUnion = bounds is null ? null : 0.35,
                    Label = Clean(GetString(suppressed, "candidateLabel")),
                    Text = Clean(GetString(suppressed, "suppressedByAggregateId")),
                    ObjectCategory = ReadEnum<ObjectCategory>(suppressed, "candidateCategory"),
                    ObjectKind = ReadEnum<ObjectCandidateKind>(suppressed, "candidateKind"),
                    RoutingInfluence = ReadEnum<ObjectRoutingInfluence>(suppressed, "aggregateRoutingInfluence"),
                    StructuralInfluence = ReadEnum<ObjectStructuralInfluence>(suppressed, "aggregateStructuralInfluence"),
                    ObjectCandidateId = Clean(GetString(suppressed, "objectCandidateId")),
                    SuppressedByAggregateId = Clean(GetString(suppressed, "suppressedByAggregateId")),
                    SuppressionReason = ReadEnum<RoutingSuppressionReason>(suppressed, "reason"),
                    SuppressionAction = ReadEnum<RoutingSuppressedObjectAction>(suppressed, "action"),
                    ReplacementRoutingObstacleId = Clean(GetString(suppressed, "replacementRoutingObstacleId")),
                    RoomUseHintId = Clean(GetString(suppressed, "roomUseHintId"))
                }, suppressed);
            });
    }

    private static IEnumerable<BenchmarkDetectionTarget> CreateLayerTargets(
        JsonElement root,
        BenchmarkManifestDraftOptions options)
    {
        var layerAnalysis = TryGetObject(root, "layerAnalysis");
        return OrderedByConfidence(layerAnalysis, "layers")
            .Select((layer, index) =>
            {
                var bounds = ReadBounds(layer, "bounds", options);
                return WithProvenance(new BenchmarkDetectionTarget
                {
                    Id = TargetId(layer, "layer", index, "name"),
                    Bounds = bounds,
                    MinIntersectionOverUnion = bounds is null ? null : 0.25,
                    Label = Clean(GetString(layer, "name")),
                    LayerCategory = ReadEnum<LayerCategory>(layer, "likelyCategory")
                }, layer);
            });
    }

    private static BenchmarkDetectorMetricExpectations CreateMetric(
        IEnumerable<BenchmarkDetectionTarget> targets,
        BenchmarkManifestDraftOptions options)
    {
        var cappedTargets = targets
            .Where(HasUsefulCriteria)
            .Take(options.MaxTargetsPerDetector)
            .ToArray();

        if (cappedTargets.Length == 0)
        {
            return new BenchmarkDetectorMetricExpectations();
        }

        return new BenchmarkDetectorMetricExpectations
        {
            Targets = cappedTargets,
            MinRecall = options.TargetRecall,
            MinPrecision = options.TargetPrecision
        };
    }

    private static BenchmarkDetectionTarget WithProvenance(
        BenchmarkDetectionTarget target,
        JsonElement element,
        params string[] sourceIdPropertyNames)
    {
        if (sourceIdPropertyNames.Length == 0)
        {
            sourceIdPropertyNames = new[] { "sourcePrimitiveIds" };
        }

        var sourcePrimitiveIds = ReadStringArray(element, sourceIdPropertyNames);
        var sourceLayers = ReadStringArray(element, "sourceLayers");
        var evidence = ReadStringArray(element, "evidence");

        return target with
        {
            Confidence = GetDouble(element, "confidence"),
            SourcePrimitiveIds = sourcePrimitiveIds.Count == 0 ? null : sourcePrimitiveIds,
            SourceLayers = sourceLayers.Count == 0 ? null : sourceLayers,
            Evidence = evidence.Count == 0 ? null : evidence
        };
    }

    private static bool HasUsefulCriteria(BenchmarkDetectionTarget target) =>
        target.PageNumber is not null
        || target.Bounds is not null
        || !string.IsNullOrWhiteSpace(target.Label)
        || !string.IsNullOrWhiteSpace(target.Text)
        || !string.IsNullOrWhiteSpace(target.Marker)
        || target.MinCount is not null
        || target.RequiresReview is not null
        || target.RegionKind is not null
        || target.DimensionKind is not null
        || target.DimensionOrientation is not null
        || target.AnnotationKind is not null
        || target.GridAxisOrientation is not null
        || target.OpeningType is not null
        || target.OpeningOperation is not null
        || target.ObjectCategory is not null
        || target.ObjectKind is not null
        || target.LayerCategory is not null
        || target.RoutingSourceKind is not null
        || target.RoutingObstacleKind is not null
        || target.RoutingInfluence is not null
        || target.StructuralInfluence is not null
        || target.RoomUseKind is not null
        || target.SuppressesChildObjects is not null
        || !string.IsNullOrWhiteSpace(target.ObjectCandidateId)
        || !string.IsNullOrWhiteSpace(target.SuppressedByAggregateId)
        || target.SuppressionReason is not null
        || target.SuppressionAction is not null
        || !string.IsNullOrWhiteSpace(target.ReplacementRoutingObstacleId)
        || !string.IsNullOrWhiteSpace(target.RoomUseHintId)
        || target.DetectedTags is { Count: > 0 };

    private static void ValidateOptions(BenchmarkManifestDraftOptions options)
    {
        if (options.MaxTargetsPerDetector < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxTargetsPerDetector must be zero or greater.");
        }

        if (options.TargetRecall < 0 || options.TargetRecall > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "TargetRecall must be between 0 and 1.");
        }

        if (options.TargetPrecision is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "TargetPrecision must be between 0 and 1 when set.");
        }
    }

    private static void AddProperty(
        IDictionary<string, string> properties,
        string name,
        string? value)
    {
        value = Clean(value);
        if (value is not null)
        {
            properties[name] = value;
        }
    }

    private static IEnumerable<JsonElement> OrderedByConfidence(JsonElement? element, string propertyName) =>
        EnumerateArray(element, propertyName)
            .OrderByDescending(item => GetDouble(item, "confidence") ?? 0)
            .ThenBy(item => GetString(item, "id") ?? GetString(item, "name") ?? string.Empty, StringComparer.Ordinal);

    private static int CountAnnotationReferences(JsonElement root) =>
        EnumerateArray(root, "annotations")
            .Sum(annotation => EnumerateArray(annotation, "items")
                .Sum(item => CountArray(item, "references")));

    private static int CountRoutingItems(JsonElement root)
    {
        var routingLayer = TryGetObject(root, "routingLayer");
        return CountArray(routingLayer, "barriers")
            + CountArray(routingLayer, "passages")
            + CountArray(routingLayer, "obstacles")
            + CountArray(routingLayer, "roomUseHints");
    }

    private static int CountRoutingSuppressedObjects(JsonElement root)
    {
        var routingLayer = TryGetObject(root, "routingLayer");
        return CountArray(routingLayer, "suppressedObjects");
    }

    private static int CountQualityIssues(JsonElement? quality, string codePrefix) =>
        EnumerateArray(quality, "issues")
            .Count(issue => GetString(issue, "code")?.StartsWith(codePrefix, StringComparison.OrdinalIgnoreCase) == true);

    private static IReadOnlyDictionary<string, int> CountReviewQueueKinds(JsonElement root) =>
        EnumerateArray(root, "reviewQueue")
            .Select(item => GetString(item, "kind"))
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .GroupBy(kind => kind!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static int CountArray(JsonElement? element, string propertyName)
    {
        var array = TryGetArray(element, propertyName);
        return array is null ? 0 : array.Value.GetArrayLength();
    }

    private static IEnumerable<JsonElement> EnumerateArray(JsonElement? element, string propertyName)
    {
        var array = TryGetArray(element, propertyName);
        if (array is null)
        {
            return Array.Empty<JsonElement>();
        }

        return array.Value.EnumerateArray().ToArray();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, params string[] propertyNames)
    {
        var values = new List<string>();
        foreach (var propertyName in propertyNames)
        {
            foreach (var item in EnumerateArray(element, propertyName))
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = Clean(item.GetString());
                if (value is not null && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    private static IReadOnlyList<string>? ReadDetectedTags(JsonElement element)
    {
        var values = new List<string>();
        AddString(values, GetString(element, "detectedTag"));
        foreach (var tag in ReadStringArray(element, "detectedTags"))
        {
            AddString(values, tag);
        }

        return values.Count == 0 ? null : values;
    }

    private static void AddString(List<string> values, string? value)
    {
        value = Clean(value);
        if (value is not null && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(value);
        }
    }

    private static JsonElement? TryGetObject(JsonElement? element, string propertyName)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.Value.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Object)
            {
                return property.Value;
            }
        }

        return null;
    }

    private static JsonElement? TryGetArray(JsonElement? element, string propertyName)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.Value.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Array)
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement? element, string propertyName)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.Value.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => Clean(property.Value.GetString()),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        return null;
    }

    private static int? GetInt(JsonElement? element, string propertyName)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.Value.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out var number))
            {
                return number;
            }

            var text = GetString(element, propertyName);
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        return null;
    }

    private static double? GetDouble(JsonElement? element, string propertyName)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.Value.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetDouble(out var number))
            {
                return number;
            }

            var text = GetString(element, propertyName);
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        return null;
    }

    private static bool? GetBool(JsonElement? element, string propertyName)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.Value.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(property.Value.GetString(), out var parsed) => parsed,
                _ => null
            };
        }

        return null;
    }

    private static T? ReadEnum<T>(JsonElement? element, string propertyName)
        where T : struct, Enum
    {
        var value = GetString(element, propertyName);
        return Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : null;
    }

    private static PlanRect? ReadBounds(
        JsonElement element,
        string propertyName,
        BenchmarkManifestDraftOptions options)
    {
        if (!options.IncludeBounds)
        {
            return null;
        }

        var bounds = TryGetObject(element, propertyName);
        if (bounds is null)
        {
            return null;
        }

        var x = GetDouble(bounds, "x");
        var y = GetDouble(bounds, "y");
        var width = GetDouble(bounds, "width");
        var height = GetDouble(bounds, "height");
        if (x is null || y is null || width is null || height is null || width <= 0 || height <= 0)
        {
            return null;
        }

        return new PlanRect(x.Value, y.Value, width.Value, height.Value);
    }

    private static int? SinglePageNumber(JsonElement element)
    {
        var pages = TryGetArray(element, "pageNumbers");
        if (pages is null || pages.Value.GetArrayLength() != 1)
        {
            return null;
        }

        var page = pages.Value[0];
        return page.ValueKind == JsonValueKind.Number && page.TryGetInt32(out var value) ? value : null;
    }

    private static string TargetId(
        JsonElement element,
        string prefix,
        int index,
        string sourcePropertyName = "id")
    {
        var sourceId = Clean(GetString(element, sourcePropertyName))
            ?? Clean(GetString(element, "id"))
            ?? $"{prefix}-{index + 1}";
        return $"{prefix}:{sourceId}";
    }

    private static int? Positive(int count) => count > 0 ? count : null;

    private static int? PositiveOrZero(int count) => count >= 0 ? count : null;

    private static double? MeasurementOutlierRatio(int? checkedCount, int? outlierCount) =>
        checkedCount is > 0 && outlierCount is >= 0
            ? Math.Round(outlierCount.Value / (double)checkedCount.Value, 4)
            : null;

    private static double? ConfidenceFloor(double? confidence) =>
        confidence is null ? null : Math.Round(Math.Max(0, confidence.Value - 0.1), 2);

    private static PlanScanQualityGrade? RelaxQualityGrade(PlanScanQualityGrade? grade) =>
        grade switch
        {
            PlanScanQualityGrade.Strong => PlanScanQualityGrade.Usable,
            PlanScanQualityGrade.Usable => PlanScanQualityGrade.ReviewRequired,
            PlanScanQualityGrade.ReviewRequired => PlanScanQualityGrade.Poor,
            PlanScanQualityGrade.Poor => PlanScanQualityGrade.Poor,
            PlanScanQualityGrade.Unknown => PlanScanQualityGrade.Poor,
            _ => null
        };

    private static PlanImportReadinessGrade? RelaxImportReadinessGrade(PlanImportReadinessGrade? grade) =>
        grade switch
        {
            PlanImportReadinessGrade.Strong => PlanImportReadinessGrade.Usable,
            PlanImportReadinessGrade.Usable => PlanImportReadinessGrade.ReviewRequired,
            PlanImportReadinessGrade.ReviewRequired => PlanImportReadinessGrade.ReviewRequired,
            PlanImportReadinessGrade.Blocked => PlanImportReadinessGrade.Blocked,
            PlanImportReadinessGrade.Unknown => PlanImportReadinessGrade.Blocked,
            _ => null
        };

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
