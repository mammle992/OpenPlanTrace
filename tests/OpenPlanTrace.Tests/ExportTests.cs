using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class ExportTests
{
    [Fact]
    public async Task JsonExporter_WritesSchemaVersionedScanResult()
    {
        var result = await CreateScanResultAsync();

        var json = PlanTraceJsonExporter.Serialize(result);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            PlanTraceExport.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetString());
        var coordinateSystem = document.RootElement.GetProperty("coordinateSystem");
        Assert.Equal("OpenPlanTracePageCoordinates", coordinateSystem.GetProperty("coordinateSpace").GetString());
        Assert.Equal("TopLeft", coordinateSystem.GetProperty("origin").GetString());
        Assert.Equal("Right", coordinateSystem.GetProperty("xAxisDirection").GetString());
        Assert.Equal("Down", coordinateSystem.GetProperty("yAxisDirection").GetString());
        Assert.Equal(1, coordinateSystem.GetProperty("pageFrames").GetArrayLength());
        Assert.Equal(500, coordinateSystem.GetProperty("pageFrames")[0].GetProperty("width").GetDouble());
        Assert.Equal(400, coordinateSystem.GetProperty("pageFrames")[0].GetProperty("height").GetDouble());
        Assert.True(document.RootElement.GetProperty("primitiveSources").GetArrayLength() >= 5);
        Assert.True(document.RootElement.GetProperty("layerAnalysis").GetProperty("layers").GetArrayLength() >= 1);
        Assert.True(document.RootElement.TryGetProperty("calibration", out _));
        var measurementConsistency = document.RootElement.GetProperty("measurementConsistency");
        Assert.True(measurementConsistency.GetProperty("outlierRatio").GetDouble() >= 0);
        Assert.True(measurementConsistency.GetProperty("hasBlockingOutliers").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(measurementConsistency.GetProperty("hasTolerableOutliers").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.Equal(MeasurementConsistencyReport.NonBlockingOutlierCountMaximum, measurementConsistency.GetProperty("nonBlockingOutlierCountMaximum").GetInt32());
        Assert.Equal(MeasurementConsistencyReport.NonBlockingOutlierRatioMaximum, measurementConsistency.GetProperty("nonBlockingOutlierRatioMaximum").GetDouble());
        Assert.Equal(MeasurementConsistencyReport.BlockingScaleSpreadRatioThreshold, measurementConsistency.GetProperty("blockingScaleSpreadRatioThreshold").GetDouble());
        Assert.False(string.IsNullOrWhiteSpace(measurementConsistency.GetProperty("metricImportImpact").GetString()));
        Assert.True(document.RootElement.TryGetProperty("titleBlocks", out _));
        Assert.True(document.RootElement.TryGetProperty("dimensions", out _));
        Assert.True(document.RootElement.TryGetProperty("annotations", out _));
        Assert.True(document.RootElement.TryGetProperty("gridAxes", out _));
        Assert.True(document.RootElement.TryGetProperty("gridBaySpacings", out _));
        Assert.True(document.RootElement.TryGetProperty("roomAdjacencyGraph", out _));
        var importReadiness = document.RootElement.GetProperty("importReadiness");
        Assert.False(string.IsNullOrWhiteSpace(importReadiness.GetProperty("grade").GetString()));
        Assert.InRange(importReadiness.GetProperty("score").GetDouble(), 0, 1);
        Assert.True(importReadiness.GetProperty("readyForGeometryImport").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(importReadiness.GetProperty("readyForMetricImport").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(importReadiness.GetProperty("readyForRoutingImport").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(importReadiness.GetProperty("requiresReview").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.Equal(JsonValueKind.Array, importReadiness.GetProperty("blockingIssueCodes").ValueKind);
        Assert.Equal(JsonValueKind.Array, importReadiness.GetProperty("reviewIssueCodes").ValueKind);
        Assert.True(importReadiness.GetProperty("recommendedActions").GetArrayLength() > 0);
        Assert.True(importReadiness.GetProperty("evidence").GetArrayLength() > 0);
        Assert.False(importReadiness.TryGetProperty("parsedGrade", out _));
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("reviewQueue").ValueKind);
        Assert.True(document.RootElement.GetProperty("walls").GetArrayLength() >= 4);
        Assert.True(document.RootElement.GetProperty("wallGraph").GetProperty("nodes").GetArrayLength() >= 4);

        var firstPrimitiveSource = document.RootElement.GetProperty("primitiveSources")[0];
        Assert.Equal("Wall", firstPrimitiveSource.GetProperty("metadata").GetProperty("layer").GetString());

        var wallLayer = document.RootElement
            .GetProperty("layerAnalysis")
            .GetProperty("layers")
            .EnumerateArray()
            .First(layer => layer.GetProperty("name").GetString() == "Wall");
        Assert.Equal("Wall", wallLayer.GetProperty("likelyCategory").GetString());
        var wallLayerScores = wallLayer.GetProperty("categoryScores").EnumerateArray().ToArray();
        Assert.Contains(wallLayerScores, score => score.GetProperty("category").GetString() == "Wall");
        Assert.True(wallLayerScores[0].GetProperty("evidence").GetArrayLength() > 0);

        var firstWall = document.RootElement.GetProperty("walls")[0];
        Assert.Equal("SingleLine", firstWall.GetProperty("detectionKind").GetString());
        Assert.False(firstWall.GetProperty("wallComponentId").ValueKind == JsonValueKind.Null);
        Assert.Equal("MainStructural", firstWall.GetProperty("wallComponentKind").GetString());
        Assert.False(firstWall.GetProperty("excludedFromStructuralTopology").GetBoolean());
        Assert.True(firstWall.GetProperty("sourcePrimitiveIds").GetArrayLength() > 0);
        Assert.True(firstWall.GetProperty("evidence").GetArrayLength() > 0);
        Assert.Contains("Wall", firstWall.GetProperty("sourceLayers").EnumerateArray().Select(layer => layer.GetString()));
    }

    [Fact]
    public async Task SvgRenderer_WritesLayeredOverlayForPage()
    {
        var result = await CreateScanResultAsync();

        var svg = PlanOverlaySvgRenderer.RenderPage(result, 1);

        Assert.Contains("<svg", svg);
        Assert.Contains("id=\"walls\"", svg);
        Assert.Contains("wall-main", svg);
        Assert.Contains("vector-effect: non-scaling-stroke", svg);
        Assert.Contains("r=\"2\"", svg);
        Assert.Contains("diagnostic-bg", svg);
        Assert.Contains("id=\"rooms\"", svg);
        Assert.Contains("id=\"room-adjacency\"", svg);
        Assert.Contains("id=\"annotation-references\"", svg);
        Assert.Contains("annotation-reference", svg);
        Assert.Contains("page:1:wall:", svg);
    }

    [Fact]
    public async Task VisualSnapshotExporter_WritesPerPageLayerBoundsAndIssues()
    {
        var result = await CreateScanResultAsync();
        var svgPaths = new Dictionary<int, string>
        {
            [1] = "overlays/page-1.svg"
        };

        var snapshot = PlanOverlaySnapshot.From(result, svgPaths);

        Assert.Equal(PlanOverlaySnapshot.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, snapshot.ScanSchemaVersion);
        Assert.Equal("OpenPlanTracePageCoordinates", snapshot.CoordinateSpace);
        Assert.Equal("TopLeft", snapshot.Origin);
        Assert.Equal("Right", snapshot.XAxisDirection);
        Assert.Equal("Down", snapshot.YAxisDirection);
        Assert.Equal(result.Quality.Grade.ToString(), snapshot.QualityGrade);
        Assert.True(snapshot.ReviewQueueCount >= 0);
        Assert.NotNull(snapshot.ReviewQueueKindBreakdown);
        Assert.NotNull(snapshot.ReviewQueueSeverityBreakdown);

        var page = Assert.Single(snapshot.Pages);
        Assert.Equal(1, page.PageNumber);
        Assert.Equal(500, page.Width);
        Assert.Equal(400, page.Height);
        Assert.Equal(0, page.PageBounds.X);
        Assert.Equal(500, page.PageBounds.Right);
        Assert.Equal("overlays/page-1.svg", page.SvgPath);
        Assert.True(page.DrawableItemCount > 0);
        Assert.True(page.PrimitiveCount >= 5);
        Assert.InRange(page.DetectionCoverage, 0, 1);
        Assert.True(page.ReviewQueueCount >= 0);
        Assert.NotNull(page.ReviewQueue);
        Assert.NotNull(page.ReviewQueueKindBreakdown);
        Assert.NotNull(page.ReviewQueueSeverityBreakdown);

        var walls = page.Layers.Single(layer => layer.Name == "walls");
        Assert.True(walls.Count >= 4);
        Assert.False(walls.Bounds.IsEmpty);
        Assert.True(walls.AverageConfidence is > 0);

        var wallComponents = page.Layers.Single(layer => layer.Name == "wallComponents");
        Assert.True(wallComponents.Count >= 1);
        Assert.Contains("MainStructural", wallComponents.Breakdown.Keys);

        var rooms = page.Layers.Single(layer => layer.Name == "rooms");
        Assert.True(rooms.Count >= 1);
        Assert.False(rooms.Bounds.IsEmpty);

        var json = PlanOverlaySnapshotJsonExporter.Serialize(
            snapshot,
            new PlanOverlaySnapshotJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            PlanOverlaySnapshot.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(
            "overlays/page-1.svg",
            document.RootElement.GetProperty("pages")[0].GetProperty("svgPath").GetString());
        Assert.Contains(
            document.RootElement.GetProperty("pages")[0].GetProperty("layers").EnumerateArray(),
            layer => layer.GetProperty("name").GetString() == "walls"
                && layer.GetProperty("count").GetInt32() >= 4);
        Assert.Equal(
            snapshot.ReviewQueueCount,
            document.RootElement.GetProperty("reviewQueueCount").GetInt32());
    }

    [Fact]
    public async Task VisualSnapshotExporter_WritesScanReviewQueueTelemetry()
    {
        var result = WithReliableCalibrationAndMeasurement(
            await CreateScanResultAsync(),
            CreateMeasurementReport(consistentCount: 3, outlierCount: 1, spreadRatio: 1.25));

        var snapshot = PlanOverlaySnapshot.From(result);

        Assert.True(snapshot.ReviewQueueCount >= 1);
        Assert.Equal(1, snapshot.ReviewQueueKindBreakdown["MeasurementOutlier"]);
        Assert.True(snapshot.ReviewQueueSeverityBreakdown["Info"] >= 1);

        var page = Assert.Single(snapshot.Pages);
        Assert.Equal(snapshot.ReviewQueueCount, page.ReviewQueueCount);
        Assert.Equal(page.ReviewQueueCount, page.ReviewQueue.Count);
        var item = Assert.Single(page.ReviewQueue, candidate => candidate.Kind == "MeasurementOutlier");
        Assert.Equal("MeasurementOutlier", item.Kind);
        Assert.Equal("measurementConsistency", item.Detector);
        Assert.Equal("Info", item.Severity);
        Assert.Equal(10, item.Priority);
        Assert.NotEmpty(item.Evidence);
        Assert.Contains(page.Issues, issue =>
            issue.Code == "visual.scan_review_queue_present"
            && issue.Severity == "info"
            && issue.PageNumber == 1);

        var json = PlanOverlaySnapshotJsonExporter.Serialize(
            snapshot,
            new PlanOverlaySnapshotJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(snapshot.ReviewQueueCount, root.GetProperty("reviewQueueCount").GetInt32());
        Assert.Equal(1, root.GetProperty("reviewQueueKindBreakdown").GetProperty("MeasurementOutlier").GetInt32());
        Assert.Equal(page.ReviewQueueCount, root.GetProperty("pages")[0].GetProperty("reviewQueue").GetArrayLength());
    }

    [Fact]
    public async Task VisualSnapshotExporter_WritesSuppressedWallPatternReviewTelemetry()
    {
        var result = await CreateScanResultAsync();
        result = result with
        {
            Diagnostics = result.Diagnostics with
            {
                Messages = result.Diagnostics.Messages
                    .Concat(new[]
                    {
                        new PlanDiagnostic(
                            "walls.dense_orthogonal_pattern_filtered",
                            DiagnosticSeverity.Info,
                            "WallDetection",
                            "18 dense repeated surface/detail pattern line primitive(s) were kept out of wall detection.")
                        {
                            Scope = DiagnosticScope.Detection,
                            PageNumber = 1,
                            Region = new PlanRect(220, 120, 48, 72),
                            Confidence = Confidence.Medium,
                            SourcePrimitiveIds = new[] { "wall-top", "wall-right" },
                            Properties = new Dictionary<string, string>
                            {
                                ["clusterCount"] = "1",
                                ["filteredLineCount"] = "18",
                                ["patterns"] = "Horizontal:18 lines/4.25 spacing"
                            }
                        }
                    })
                    .ToArray()
            }
        };

        var snapshot = PlanOverlaySnapshot.From(result);

        Assert.Equal(1, snapshot.ReviewQueueKindBreakdown["SuppressedWallPatternReview"]);
        var page = Assert.Single(snapshot.Pages);
        var item = Assert.Single(page.ReviewQueue, candidate => candidate.Kind == "SuppressedWallPatternReview");
        Assert.Equal("WallDetection", item.Detector);
        Assert.Equal("walls.dense_orthogonal_pattern_filtered", item.ItemId);
        Assert.Equal("Info", item.Severity);
        Assert.Equal(25, item.Priority);
        Assert.Equal(2, item.SourcePrimitiveCount);
        Assert.Contains(item.Evidence, evidence => evidence.Contains("Horizontal:18 lines/4.25 spacing", StringComparison.Ordinal));
        Assert.Contains(page.Issues, issue =>
            issue.Code == "visual.scan_review_queue_present"
            && issue.Severity == "info"
            && issue.PageNumber == 1);
    }

    [Fact]
    public async Task GeoJsonExporter_WritesPageCoordinateFeatureCollection()
    {
        var result = await CreateScanResultAsync();

        var geoJson = PlanTraceGeoJsonExporter.Serialize(
            result,
            new PlanTraceGeoJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(geoJson);
        var root = document.RootElement;

        Assert.Equal("FeatureCollection", root.GetProperty("type").GetString());
        Assert.Equal(PlanTraceGeoJsonExporter.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetString());
        Assert.Equal("OpenPlanTracePageCoordinates", root.GetProperty("coordinateSpace").GetString());
        Assert.Contains("not WGS84", root.GetProperty("coordinateNote").GetString());

        var features = root.GetProperty("features").EnumerateArray().ToArray();
        Assert.Contains(features, feature => FeatureType(feature) == "page");
        Assert.Contains(features, feature => FeatureType(feature) == "region");
        Assert.Contains(features, feature => FeatureType(feature) == "annotation");
        Assert.Contains(features, feature => FeatureType(feature) == "annotationReference");

        var annotationReference = features.First(feature => FeatureType(feature) == "annotationReference");
        Assert.Equal("1", annotationReference.GetProperty("properties").GetProperty("marker").GetString());
        Assert.Contains(annotationReference.GetProperty("properties").GetProperty("sourcePrimitiveIds").EnumerateArray(), id => id.GetString() == "keynote-marker-1");
        var wall = features.First(feature => FeatureType(feature) == "wall");
        Assert.Equal("MainStructural", wall.GetProperty("properties").GetProperty("wallComponentKind").GetString());
        Assert.False(wall.GetProperty("properties").GetProperty("excludedFromStructuralTopology").GetBoolean());
        Assert.Equal("LineString", wall.GetProperty("geometry").GetProperty("type").GetString());
        Assert.Equal("Wall", wall.GetProperty("properties").GetProperty("sourceLayers")[0].GetString());
        Assert.True(wall.GetProperty("properties").GetProperty("sourcePrimitiveIds").GetArrayLength() > 0);

        var room = features.First(feature => FeatureType(feature) == "room");
        Assert.Equal("Polygon", room.GetProperty("geometry").GetProperty("type").GetString());
        Assert.Equal("ROOM", room.GetProperty("properties").GetProperty("label").GetString());
        var ring = room
            .GetProperty("geometry")
            .GetProperty("coordinates")[0]
            .EnumerateArray()
            .ToArray();
        Assert.True(ring.Length >= 4);
        Assert.Equal(ring[0].GetRawText(), ring[^1].GetRawText());
    }

    [Fact]
    public async Task PlacementExporter_WritesDownstreamCoordinateContract()
    {
        var result = await CreateScanResultAsync();

        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(placementJson);
        var root = document.RootElement;

        Assert.Equal(PlanPlacementExport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetString());
        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, root.GetProperty("scanSchemaVersion").GetString());
        var placementDocument = root.GetProperty("document");
        Assert.Equal("export-test.pdf", placementDocument.GetProperty("sourceName").GetString());
        Assert.Equal("pdf", placementDocument.GetProperty("sourceFormat").GetString());
        Assert.Equal("PDF/PdfPig", placementDocument.GetProperty("loader").GetString());
        Assert.Equal("Pdf", placementDocument.GetProperty("sourceKind").GetString());
        Assert.Equal("Pdf", placementDocument.GetProperty("effectiveSourceKind").GetString());
        Assert.False(placementDocument.GetProperty("isDwgDerived").GetBoolean());
        Assert.Equal("pdf", placementDocument.GetProperty("properties").GetProperty("format").GetString());
        var summary = root.GetProperty("summary");
        Assert.Equal(root.GetProperty("pages").GetArrayLength(), summary.GetProperty("pageCount").GetInt32());
        Assert.Equal(root.GetProperty("walls").GetArrayLength(), summary.GetProperty("wallCount").GetInt32());
        Assert.Equal(root.GetProperty("rooms").GetArrayLength(), summary.GetProperty("roomCount").GetInt32());
        Assert.Equal(root.GetProperty("openings").GetArrayLength(), summary.GetProperty("openingCount").GetInt32());
        Assert.Equal(root.GetProperty("objectAggregates").GetArrayLength(), summary.GetProperty("objectAggregateCount").GetInt32());
        Assert.Equal(
            root.GetProperty("routingLayer").GetProperty("barriers").GetArrayLength()
            + root.GetProperty("routingLayer").GetProperty("passages").GetArrayLength()
            + root.GetProperty("routingLayer").GetProperty("obstacles").GetArrayLength()
            + root.GetProperty("routingLayer").GetProperty("roomUseHints").GetArrayLength()
            + root.GetProperty("routingLayer").GetProperty("suppressedObjects").GetArrayLength(),
            summary.GetProperty("routingItemCount").GetInt32());
        Assert.True(summary.GetProperty("coordinateReadyRatio").GetDouble() >= 0);
        Assert.True(
            summary.GetProperty("metricReadyEntityCount").GetInt32()
            <= summary.GetProperty("coordinateReadyEntityCount").GetInt32());
        var importReadiness = summary.GetProperty("importReadiness");
        Assert.False(string.IsNullOrWhiteSpace(importReadiness.GetProperty("grade").GetString()));
        Assert.InRange(importReadiness.GetProperty("score").GetDouble(), 0, 1);
        var readyForGeometryImport = importReadiness.GetProperty("readyForGeometryImport").GetBoolean();
        var readyForMetricImport = importReadiness.GetProperty("readyForMetricImport").GetBoolean();
        var readyForRoutingImport = importReadiness.GetProperty("readyForRoutingImport").GetBoolean();
        Assert.True(!readyForMetricImport || readyForGeometryImport);
        Assert.True(!readyForRoutingImport || readyForGeometryImport);
        Assert.True(importReadiness.GetProperty("blockingIssueCodes").ValueKind == JsonValueKind.Array);
        Assert.True(importReadiness.GetProperty("reviewIssueCodes").ValueKind == JsonValueKind.Array);
        Assert.True(importReadiness.GetProperty("recommendedActions").ValueKind == JsonValueKind.Array);
        Assert.True(importReadiness.GetProperty("evidence").GetArrayLength() > 0);
        Assert.Equal(root.GetProperty("pages").GetArrayLength(), summary.GetProperty("pageSummaries").GetArrayLength());
        Assert.True(summary.GetProperty("pageSummaries")[0].TryGetProperty("detectionBounds", out _));
        Assert.Equal("OpenPlanTracePageCoordinates", root.GetProperty("coordinateSystem").GetProperty("coordinateSpace").GetString());
        Assert.Equal("TopLeft", root.GetProperty("coordinateSystem").GetProperty("origin").GetString());
        Assert.Equal("PDF/DXF page coordinate space after OpenPlanTrace normalization", root.GetProperty("coordinateSystem").GetProperty("geometryBasis").GetString());
        Assert.Equal("double", root.GetProperty("coordinateSystem").GetProperty("precision").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("coordinateSystem").GetProperty("realWorldUnit").GetString()));
        Assert.Equal(1, root.GetProperty("coordinateSystem").GetProperty("pageFrames").GetArrayLength());
        Assert.Equal(6, root.GetProperty("coordinateSystem").GetProperty("pageFrames")[0].GetProperty("pageToNormalizedTransform").GetArrayLength());
        Assert.Equal(6, root.GetProperty("coordinateSystem").GetProperty("pageFrames")[0].GetProperty("normalizedToPageTransform").GetArrayLength());
        Assert.True(root.GetProperty("qualityGate").TryGetProperty("readyForCoordinatePlacement", out _));
        Assert.True(root.GetProperty("qualityGate").TryGetProperty("readyForMetricPlacement", out _));
        Assert.Equal(1, root.GetProperty("pages").GetArrayLength());

        var wall = root.GetProperty("walls")[0];
        Assert.Equal("page:1:wall:1", wall.GetProperty("id").GetString());
        Assert.Equal("Wall", wall.GetProperty("sourceLayers")[0].GetString());
        Assert.Equal("MainStructural", wall.GetProperty("wallComponentKind").GetString());
        Assert.True(wall.GetProperty("centerLine").GetProperty("start").GetProperty("x").GetDouble() > 0);
        Assert.True(wall.GetProperty("reliability").GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.True(wall.GetProperty("reliability").GetProperty("reasons").ValueKind == JsonValueKind.Array);

        var room = root.GetProperty("rooms")[0];
        Assert.Equal("ROOM", room.GetProperty("label").GetString());
        Assert.True(room.GetProperty("boundary").GetArrayLength() >= 4);

        Assert.True(root.GetProperty("routingLayer").TryGetProperty("barriers", out _));
        Assert.True(root.GetProperty("issues").ValueKind == JsonValueKind.Array);
    }

    [Fact]
    public async Task PlacementExporter_TreatsBoundedMeasurementOutliersAsMetricReadyWithReview()
    {
        var result = WithReliableCalibrationAndMeasurement(
            await CreateScanResultAsync(),
            CreateMeasurementReport(consistentCount: 6, outlierCount: 2, spreadRatio: 1.37));

        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(placementJson);
        var root = document.RootElement;

        Assert.Equal("AvailableWithOutlierReview", root.GetProperty("calibration").GetProperty("metricCoordinateStatus").GetString());

        var qualityGate = root.GetProperty("qualityGate");
        Assert.True(qualityGate.GetProperty("readyForMetricPlacement").GetBoolean());
        Assert.Equal("CalibratedWithOutlierReview", qualityGate.GetProperty("metricTrust").GetString());

        var importReadiness = root.GetProperty("summary").GetProperty("importReadiness");
        Assert.True(importReadiness.GetProperty("readyForMetricImport").GetBoolean());
        Assert.Contains(
            "placement.measurement_outliers.require_review",
            JsonStrings(importReadiness.GetProperty("reviewIssueCodes")));
        Assert.DoesNotContain(
            "placement.import.measurement_outliers",
            JsonStrings(importReadiness.GetProperty("blockingIssueCodes")));
        Assert.Contains(
            importReadiness.GetProperty("evidence").EnumerateArray(),
            item => item.GetString()?.Contains("requires review without blocking metric import", StringComparison.Ordinal) == true);

        var measurementIssue = root.GetProperty("issues").EnumerateArray().Single(issue =>
            issue.GetProperty("code").GetString() == "placement.measurement_outliers.require_review");
        Assert.Equal("review", measurementIssue.GetProperty("properties").GetProperty("metricImportImpact").GetString());
        Assert.Equal("0.25", measurementIssue.GetProperty("properties").GetProperty("outlierRatio").GetString());
    }

    [Fact]
    public async Task PlacementExporter_WritesEvidenceBackedPlacementIssues()
    {
        var result = WithReliableCalibrationAndMeasurement(
            await CreateScanResultAsync(),
            CreateMeasurementReport(consistentCount: 4, outlierCount: 0, spreadRatio: 1.02));
        result = result with
        {
            Openings = result.Openings
                .Concat(new[]
                {
                    new OpeningCandidate(
                        "opening-needs-host",
                        1,
                        OpeningType.Door,
                        new PlanRect(390, 100, 24, 12),
                        Confidence.Medium)
                    {
                        Operation = OpeningOperation.Unknown,
                        CenterLine = new PlanLineSegment(new PlanPoint(390, 106), new PlanPoint(414, 106)),
                        SourcePrimitiveIds = new[] { "opening-src" },
                        Evidence = new[] { "synthetic opening needs review" }
                    }
                })
                .ToArray(),
            Diagnostics = result.Diagnostics with
            {
                Messages = result.Diagnostics.Messages
                    .Concat(new[]
                    {
                        new PlanDiagnostic(
                            "walls.dense_orthogonal_pattern_filtered",
                            DiagnosticSeverity.Info,
                            "WallDetection",
                            "12 dense repeated surface/detail pattern line primitive(s) were kept out of wall detection.")
                        {
                            Scope = DiagnosticScope.Detection,
                            PageNumber = 1,
                            Region = new PlanRect(240, 160, 42, 96),
                            Confidence = Confidence.Medium,
                            SourcePrimitiveIds = new[] { "wall-top", "wall-right", "wall-bottom" },
                            Properties = new Dictionary<string, string>
                            {
                                ["clusterCount"] = "1",
                                ["filteredLineCount"] = "12",
                                ["patterns"] = "Horizontal:12 lines/4.25 spacing"
                            }
                        },
                        new PlanDiagnostic(
                            "wall_graph.endpoint_gap.review",
                            DiagnosticSeverity.Warning,
                            "wall-graph",
                            "A wall graph endpoint nearly touches another wall endpoint or host wall but was not safely snapped.")
                        {
                            Scope = DiagnosticScope.Detection,
                            PageNumber = 1,
                            Region = new PlanRect(180, 92, 28, 24),
                            Confidence = Confidence.Medium,
                            SourcePrimitiveIds = new[] { "wall-top", "wall-right" },
                            Properties = new Dictionary<string, string>
                            {
                                ["gapKind"] = "EndpointToWall",
                                ["gapDistance"] = "12",
                                ["nodeId"] = "page:1:node:5",
                                ["hostWallId"] = "page:1:wall:2",
                                ["wallIds"] = "page:1:wall:1,page:1:wall:2"
                            }
                        }
                    })
                    .ToArray()
            }
        };

        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(placementJson);
        var importReadiness = document.RootElement
            .GetProperty("summary")
            .GetProperty("importReadiness");
        Assert.Contains(
            "placement.wall_graph.endpoint_gaps.require_review",
            JsonStrings(importReadiness.GetProperty("reviewIssueCodes")));
        Assert.DoesNotContain(
            "placement.review.wall_graph_endpoint_gap",
            JsonStrings(importReadiness.GetProperty("reviewIssueCodes")));
        Assert.DoesNotContain(
            "quality.diagnostic_warnings",
            JsonStrings(importReadiness.GetProperty("reviewIssueCodes")));
        Assert.DoesNotContain(
            "quality.object_groups_require_review",
            JsonStrings(importReadiness.GetProperty("reviewIssueCodes")));

        var issues = document.RootElement.GetProperty("issues").EnumerateArray().ToArray();

        var suppressed = Assert.Single(issues, issue =>
            issue.GetProperty("code").GetString() == "placement.review.suppressed_wall_pattern");
        Assert.Equal(1, suppressed.GetProperty("pageNumber").GetInt32());
        Assert.Equal(new[] { 1 }, JsonInts(suppressed.GetProperty("pageNumbers")));
        Assert.Equal(240, suppressed.GetProperty("bounds").GetProperty("x").GetDouble());
        Assert.Equal(2400, suppressed.GetProperty("boundsMillimeters").GetProperty("x").GetDouble(), precision: 3);
        Assert.Contains("Wall", JsonStrings(suppressed.GetProperty("sourceLayers")));
        Assert.Contains("wall-top", JsonStrings(suppressed.GetProperty("sourcePrimitiveIds")));
        Assert.Contains(
            suppressed.GetProperty("evidence").EnumerateArray(),
            item => item.GetString()?.Contains("Horizontal:12 lines/4.25 spacing", StringComparison.Ordinal) == true);
        Assert.Equal("walls.dense_orthogonal_pattern_filtered", suppressed.GetProperty("properties").GetProperty("diagnosticCode").GetString());
        Assert.Contains("dense/detail area", suppressed.GetProperty("recommendedAction").GetString());

        var wallGap = Assert.Single(issues, issue =>
            issue.GetProperty("code").GetString() == "placement.review.wall_graph_endpoint_gap");
        Assert.Equal(180, wallGap.GetProperty("bounds").GetProperty("x").GetDouble());
        Assert.Equal(1800, wallGap.GetProperty("boundsMillimeters").GetProperty("x").GetDouble(), precision: 3);
        Assert.Equal("EndpointToWall", wallGap.GetProperty("properties").GetProperty("gapKind").GetString());
        Assert.Contains("wall-top", JsonStrings(wallGap.GetProperty("sourcePrimitiveIds")));
        Assert.Contains("unsnapped wall junction", wallGap.GetProperty("recommendedAction").GetString());

        var unanchoredOpening = Assert.Single(issues, issue =>
            issue.GetProperty("code").GetString() == "placement.opening.unanchored"
            && issue.GetProperty("itemId").GetString() == "opening-needs-host");
        Assert.Equal(3900, unanchoredOpening.GetProperty("boundsMillimeters").GetProperty("x").GetDouble(), precision: 3);
        Assert.Equal("Unknown", unanchoredOpening.GetProperty("properties").GetProperty("operation").GetString());
        Assert.Contains("host-wall", unanchoredOpening.GetProperty("recommendedAction").GetString());

        Assert.All(issues, issue =>
        {
            Assert.True(issue.GetProperty("pageNumbers").ValueKind == JsonValueKind.Array);
            Assert.True(issue.GetProperty("bounds").ValueKind is JsonValueKind.Object or JsonValueKind.Null);
            Assert.True(issue.GetProperty("boundsMillimeters").ValueKind is JsonValueKind.Object or JsonValueKind.Null);
            Assert.False(string.IsNullOrWhiteSpace(issue.GetProperty("recommendedAction").GetString()));
            Assert.True(issue.GetProperty("sourcePrimitiveIds").ValueKind == JsonValueKind.Array);
            Assert.True(issue.GetProperty("sourceLayers").ValueKind == JsonValueKind.Array);
            Assert.True(issue.GetProperty("evidence").ValueKind == JsonValueKind.Array);
            Assert.True(issue.GetProperty("properties").ValueKind == JsonValueKind.Object);
        });
    }

    [Fact]
    public async Task JsonExporter_WritesEvidenceBackedScanReviewQueue()
    {
        var result = await CreateScanResultAsync();
        result = result with
        {
            Dimensions = new[]
            {
                new DimensionAnnotation(
                    "dimension-outlier",
                    1,
                    DimensionKind.Linear,
                    DimensionOrientation.Horizontal,
                    "3000 mm",
                    "3000 mm",
                    new PlanRect(120, 300, 80, 16),
                    PlanMeasurementUnit.Millimeter,
                    3000,
                    new PlanLineSegment(new PlanPoint(100, 320), new PlanPoint(220, 320)),
                    120,
                    25,
                    Confidence.High,
                    null,
                    new[] { "dimension-outlier-src" },
                    new[] { "Synthetic outlier dimension." })
            },
            MeasurementConsistency = new MeasurementConsistencyReport(
                HasReliableCalibration: true,
                SelectedMillimetersPerDrawingUnit: 10,
                MedianDimensionMillimetersPerDrawingUnit: 10,
                DimensionScaleSpreadRatio: 1.2,
                Confidence: Confidence.High,
                Checks: new[]
                {
                    new MeasurementConsistencyCheck(
                        "dimension-consistent-1",
                        1,
                        MeasurementConsistencyStatus.Consistent,
                        1000,
                        100,
                        10,
                        10,
                        1000,
                        0,
                        0,
                        Confidence.High,
                        new[] { "dimension-consistent-src-1" },
                        new[] { "Dimension is consistent with selected calibration." }),
                    new MeasurementConsistencyCheck(
                        "dimension-consistent-2",
                        1,
                        MeasurementConsistencyStatus.Consistent,
                        1000,
                        100,
                        10,
                        10,
                        1000,
                        0,
                        0,
                        Confidence.High,
                        new[] { "dimension-consistent-src-2" },
                        new[] { "Dimension is consistent with selected calibration." }),
                    new MeasurementConsistencyCheck(
                        "dimension-consistent-3",
                        1,
                        MeasurementConsistencyStatus.Consistent,
                        1000,
                        100,
                        10,
                        10,
                        1000,
                        0,
                        0,
                        Confidence.High,
                        new[] { "dimension-consistent-src-3" },
                        new[] { "Dimension is consistent with selected calibration." }),
                    new MeasurementConsistencyCheck(
                        "dimension-outlier",
                        1,
                        MeasurementConsistencyStatus.Outlier,
                        3000,
                        120,
                        25,
                        10,
                        1200,
                        1800,
                        1.5,
                        Confidence.High,
                        new[] { "dimension-outlier-src" },
                        new[] { "Dimension conflicts with selected calibration." })
                }),
            ObjectGroups = new[]
            {
                new ObjectCandidateGroup(
                    "object-group-review",
                    "generic-symbol::a-equip",
                    ObjectCandidateKind.Symbol,
                    ObjectCategory.GenericSymbol,
                    4,
                    new PlanRect(210, 180, 24, 18),
                    new[] { 1 },
                    new[] { "object-1", "object-2" },
                    new[] { "object-group-src" },
                    true,
                    Confidence.Medium,
                    new[] { "review recommended for generic/unknown symbol group" })
            },
            ObjectAggregates = new[]
            {
                new ObjectAggregate(
                    "aggregate-review",
                    1,
                    new PlanRect(300, 180, 60, 32),
                    ObjectCategory.GenericSymbol,
                    ObjectCandidateKind.Symbol,
                    3,
                    new[] { "object-3", "object-4", "object-5" },
                    new[] { "object-group-review" },
                    new[] { "aggregate-src" },
                    ObjectRoutingInfluence.HardObstacle,
                    ObjectStructuralInfluence.NonStructural,
                    true,
                    RoomUseKind.Unknown,
                    Confidence.Medium,
                    new[] { "aggregate requires review before routing use" })
                {
                    RequiresReview = true
                }
            },
            Openings = new[]
            {
                new OpeningCandidate(
                    "opening-review",
                    1,
                    OpeningType.Door,
                    new PlanRect(390, 100, 24, 12),
                    Confidence.Medium)
                {
                    Operation = OpeningOperation.Unknown,
                    CenterLine = new PlanLineSegment(new PlanPoint(390, 106), new PlanPoint(414, 106)),
                    SourcePrimitiveIds = new[] { "opening-src" },
                    Evidence = new[] { "synthetic opening needs review" }
                }
            },
            Diagnostics = result.Diagnostics with
            {
                Messages = result.Diagnostics.Messages
                    .Concat(new[]
                    {
                        new PlanDiagnostic(
                            "walls.dense_orthogonal_pattern_filtered",
                            DiagnosticSeverity.Info,
                            "WallDetection",
                            "12 dense repeated surface/detail pattern line primitive(s) were kept out of wall detection.")
                        {
                            Scope = DiagnosticScope.Detection,
                            PageNumber = 1,
                            Region = new PlanRect(240, 160, 42, 96),
                            Confidence = Confidence.Medium,
                            SourcePrimitiveIds = new[] { "wall-top", "wall-right", "wall-bottom" },
                            Properties = new Dictionary<string, string>
                            {
                                ["clusterCount"] = "1",
                                ["parallelClusterCount"] = "1",
                                ["filteredLineCount"] = "12",
                                ["patterns"] = "Horizontal:12 lines/4.25 spacing"
                            }
                        },
                        new PlanDiagnostic(
                            "wall_graph.endpoint_gap.review",
                            DiagnosticSeverity.Warning,
                            "wall-graph",
                            "A wall graph endpoint nearly touches another wall endpoint or host wall but was not safely snapped.")
                        {
                            Scope = DiagnosticScope.Detection,
                            PageNumber = 1,
                            Region = new PlanRect(180, 92, 28, 24),
                            Confidence = Confidence.Medium,
                            SourcePrimitiveIds = new[] { "wall-top", "wall-right" },
                            Properties = new Dictionary<string, string>
                            {
                                ["gapKind"] = "EndpointToWall",
                                ["gapDistance"] = "12",
                                ["nodeId"] = "page:1:node:5",
                                ["hostWallId"] = "page:1:wall:2",
                                ["wallIds"] = "page:1:wall:1,page:1:wall:2"
                            }
                        }
                    })
                    .ToArray()
            }
        };

        var json = PlanTraceJsonExporter.Serialize(result);
        using var document = JsonDocument.Parse(json);
        var queue = document.RootElement.GetProperty("reviewQueue").EnumerateArray().ToArray();

        Assert.Contains(queue, item =>
            item.GetProperty("kind").GetString() == "MeasurementOutlier"
            && item.GetProperty("itemId").GetString() == "dimension-outlier"
            && item.GetProperty("bounds").ValueKind == JsonValueKind.Object
            && item.GetProperty("properties").GetProperty("metricImportImpact").GetString() == "ReviewOnly");
        Assert.Contains(queue, item =>
            item.GetProperty("kind").GetString() == "ObjectGroupReview"
            && item.GetProperty("itemId").GetString() == "object-group-review"
            && item.GetProperty("properties").GetProperty("count").GetString() == "4"
            && item.GetProperty("properties").GetProperty("reviewQueueRank").GetString() == "1"
            && item.GetProperty("properties").GetProperty("reviewQueueReason").GetString()!.Contains("repeated 4 occurrence", StringComparison.Ordinal));
        Assert.Contains(queue, item =>
            item.GetProperty("kind").GetString() == "ObjectAggregateReview"
            && item.GetProperty("itemId").GetString() == "aggregate-review"
            && item.GetProperty("properties").GetProperty("routingInfluence").GetString() == "HardObstacle");
        Assert.Contains(queue, item =>
            item.GetProperty("kind").GetString() == "OpeningReview"
            && item.GetProperty("itemId").GetString() == "opening-review"
            && item.GetProperty("properties").GetProperty("placementStatus").GetString() == "Unanchored");
        Assert.Contains(queue, item =>
            item.GetProperty("kind").GetString() == "SuppressedWallPatternReview"
            && item.GetProperty("detector").GetString() == "WallDetection"
            && item.GetProperty("itemId").GetString() == "walls.dense_orthogonal_pattern_filtered"
            && item.GetProperty("bounds").GetProperty("x").GetDouble() == 240
            && item.GetProperty("properties").GetProperty("parallelClusterCount").GetString() == "1"
            && item.GetProperty("recommendedAction").GetString()?.Contains("Visually verify", StringComparison.Ordinal) == true
            && item.GetProperty("evidence").EnumerateArray().Any(evidence =>
                evidence.GetString()?.Contains("Horizontal:12 lines/4.25 spacing", StringComparison.Ordinal) == true));
        Assert.Contains(queue, item =>
            item.GetProperty("kind").GetString() == "WallGraphGapReview"
            && item.GetProperty("itemId").GetString() == "wall_graph.endpoint_gap.review"
            && item.GetProperty("priority").GetInt32() == 15
            && item.GetProperty("bounds").GetProperty("x").GetDouble() == 180
            && item.GetProperty("properties").GetProperty("gapKind").GetString() == "EndpointToWall"
            && item.GetProperty("properties").GetProperty("reviewQueueRank").GetString() == "1"
            && item.GetProperty("properties").GetProperty("reviewQueueReason").GetString()!.Contains("gap 12 drawing unit", StringComparison.Ordinal)
            && item.GetProperty("recommendedAction").GetString()?.Contains("wall graph topology", StringComparison.Ordinal) == true);
        Assert.All(queue, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("recommendedAction").GetString()));
            Assert.Equal(JsonValueKind.Array, item.GetProperty("evidence").ValueKind);
            Assert.Equal(JsonValueKind.Object, item.GetProperty("properties").ValueKind);
        });
    }

    private static async Task<PlanScanResult> CreateScanResultAsync()
    {
        var document = new PlanDocument(
            "export-test",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        WallLine("wall-top", new PlanPoint(100, 100), new PlanPoint(300, 100)),
                        WallLine("wall-right", new PlanPoint(300, 100), new PlanPoint(300, 260)),
                        WallLine("wall-bottom", new PlanPoint(300, 260), new PlanPoint(100, 260)),
                        WallLine("wall-left", new PlanPoint(100, 260), new PlanPoint(100, 100)),
                        new TextPrimitive("ROOM", new PlanRect(145, 145, 48, 16)),
                        new TextPrimitive("KEYNOTES", new PlanRect(330, 50, 110, 16)) { SourceId = "notes-heading", Layer = "A-NOTE" },
                        new TextPrimitive("1. VERIFY ACCESS", new PlanRect(330, 72, 120, 16)) { SourceId = "notes-1", Layer = "A-NOTE" },
                        new TextPrimitive("1", new PlanRect(210, 145, 10, 10)) { SourceId = "keynote-marker-1", Layer = "A-ANNO" },
                        new RectanglePrimitive(new PlanRect(205, 140, 22, 22)) { SourceId = "keynote-bubble-1", Layer = "A-ANNO" }
                    })
            })
        {
            Metadata = new PlanMetadata
            {
                SourceName = "export-test.pdf",
                SourcePath = @"C:\plans\export-test.pdf",
                Properties = new Dictionary<string, string>
                {
                    ["format"] = "pdf",
                    ["loader"] = "PDF/PdfPig",
                    ["sourceKind"] = PlanSourceKind.Pdf.ToString(),
                    ["effectiveSourceKind"] = PlanSourceKind.Pdf.ToString(),
                    ["fileExtension"] = ".pdf",
                    ["contentType"] = "application/pdf"
                }
            }
        };

        return await new OpenPlanTraceScanner().ScanAsync(document);
    }

    private static LinePrimitive WallLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "Wall",
            Source = new PrimitiveSourceMetadata
            {
                SourceFormat = "test",
                SourceId = sourceId,
                EntityType = "LINE",
                Layer = "Wall",
                LineWeight = 1.0,
                DrawingSpace = SourceDrawingSpace.Model
            }
        };

    private static PlanScanResult WithReliableCalibrationAndMeasurement(
        PlanScanResult result,
        MeasurementConsistencyReport measurement) =>
        result with
        {
            Calibration = new PlanCalibration(
                PlanMeasurementUnit.DrawingUnit,
                PlanMeasurementUnit.Millimeter,
                null,
                10,
                Confidence.High,
                Array.Empty<CalibrationEvidence>(),
                Array.Empty<CalibrationScaleGroup>()),
            MeasurementConsistency = measurement,
            Quality = CreateUsableQualityReport(result, measurement)
        };

    private static PlanScanQualityReport CreateUsableQualityReport(
        PlanScanResult result,
        MeasurementConsistencyReport measurement) =>
        new(
            Confidence.High,
            PlanScanQualityGrade.Usable,
            measurement.HasOutliers,
            result.Document.Pages.Count,
            result.Document.Pages.Sum(page => page.Primitives.Count),
            result.Walls.Count + result.Rooms.Count + result.Openings.Count + result.ObjectAggregates.Count,
            4,
            4,
            true,
            0,
            measurement.HasOutliers ? 1 : 0,
            0,
            Array.Empty<PlanDetectorQualitySummary>(),
            measurement.HasOutliers
                ? new[]
                {
                    new PlanScanQualityIssue(
                        "quality.measurement_outliers",
                        DiagnosticSeverity.Warning,
                        "Synthetic bounded measurement outliers.",
                        Confidence.High,
                        new Dictionary<string, string>())
                }
                : Array.Empty<PlanScanQualityIssue>(),
            new[] { "Synthetic usable quality report." });

    private static MeasurementConsistencyReport CreateMeasurementReport(
        int consistentCount,
        int outlierCount,
        double spreadRatio) =>
        new(
            HasReliableCalibration: true,
            SelectedMillimetersPerDrawingUnit: 10,
            MedianDimensionMillimetersPerDrawingUnit: 10.2,
            DimensionScaleSpreadRatio: spreadRatio,
            Confidence: Confidence.High,
            Checks: CreateMeasurementChecks(consistentCount, outlierCount));

    private static IReadOnlyList<MeasurementConsistencyCheck> CreateMeasurementChecks(
        int consistentCount,
        int outlierCount)
    {
        var checks = new List<MeasurementConsistencyCheck>();
        for (var index = 0; index < consistentCount; index++)
        {
            checks.Add(CreateMeasurementCheck(index + 1, MeasurementConsistencyStatus.Consistent, impliedScale: 10, relativeError: 0));
        }

        for (var index = 0; index < outlierCount; index++)
        {
            checks.Add(CreateMeasurementCheck(
                consistentCount + index + 1,
                MeasurementConsistencyStatus.Outlier,
                impliedScale: 12.8,
                relativeError: 0.28));
        }

        return checks;
    }

    private static MeasurementConsistencyCheck CreateMeasurementCheck(
        int index,
        MeasurementConsistencyStatus status,
        double impliedScale,
        double relativeError) =>
        new(
            $"dimension-{index}",
            1,
            status,
            1000,
            100,
            impliedScale,
            10,
            1000,
            impliedScale == 10 ? 0 : 280,
            relativeError,
            Confidence.High,
            new[] { $"dimension-{index}" },
            new[] { $"Synthetic {status} check." });

    private static IReadOnlyList<string> JsonStrings(JsonElement array) =>
        array.EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();

    private static IReadOnlyList<int> JsonInts(JsonElement array) =>
        array.EnumerateArray()
            .Select(item => item.GetInt32())
            .ToArray();

    private static string FeatureType(JsonElement feature) =>
        feature.GetProperty("properties").GetProperty("featureType").GetString()!;
}
