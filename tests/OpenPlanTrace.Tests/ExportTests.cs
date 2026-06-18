using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenPlanTrace.Tests;

public sealed class ExportTests
{
    [Fact]
    public async Task JsonExporter_WritesSchemaVersionedScanResult()
    {
        var result = await CreateScanResultAsync();
        result = result with
        {
            Calibration = new PlanCalibration(
                PlanMeasurementUnit.DrawingUnit,
                PlanMeasurementUnit.Millimeter,
                ScaleRatio: null,
                MillimetersPerDrawingUnit: 10,
                Confidence.High,
                Array.Empty<CalibrationEvidence>(),
                Array.Empty<CalibrationScaleGroup>())
        };

        var json = PlanTraceJsonExporter.Serialize(result);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            PlanTraceExport.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetString());
        var documentExport = document.RootElement.GetProperty("document");
        Assert.Equal("pdf", documentExport.GetProperty("sourceFormat").GetString());
        Assert.Equal("PDF/PdfPig", documentExport.GetProperty("loader").GetString());
        Assert.Equal("Pdf", documentExport.GetProperty("sourceKind").GetString());
        Assert.Equal("Pdf", documentExport.GetProperty("effectiveSourceKind").GetString());
        Assert.False(documentExport.GetProperty("isDwgDerived").GetBoolean());
        Assert.False(documentExport.GetProperty("isRasterDerived").GetBoolean());
        Assert.Equal("pdf-vector", documentExport.GetProperty("ingestionPath").GetString());
        var sourceReadiness = documentExport.GetProperty("sourceReadiness");
        Assert.Equal("VectorGeometryReady", sourceReadiness.GetProperty("status").GetString());
        Assert.True(sourceReadiness.GetProperty("canUseVectorGeometry").GetBoolean());
        Assert.False(sourceReadiness.GetProperty("requiresExternalAdapter").GetBoolean());
        Assert.False(sourceReadiness.GetProperty("requiresOcr").GetBoolean());
        Assert.Contains(
            "format=pdf",
            sourceReadiness.GetProperty("evidence").EnumerateArray().Select(item => item.GetString()));
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
        var diagnostics = document.RootElement.GetProperty("diagnostics");
        var artifactPlans = diagnostics.GetProperty("artifactPlans").EnumerateArray().ToArray();
        Assert.Equal(artifactPlans.Length, diagnostics.GetProperty("artifactPlanCount").GetInt32());
        Assert.Contains(
            artifactPlans,
            item => item.GetProperty("artifact").GetString() == nameof(PlanArtifactKind.Walls)
                && item.GetProperty("isProducedByStage").GetBoolean()
                && item.GetProperty("isConsumedByStage").GetBoolean()
                && item.GetProperty("producerCount").GetInt32() == item.GetProperty("producerStages").GetArrayLength()
                && item.GetProperty("consumerCount").GetInt32() == item.GetProperty("consumerStages").GetArrayLength()
                && item.GetProperty("firstProducerWave").GetInt32() > 0
                && item.GetProperty("lastConsumerWave").GetInt32() >= item.GetProperty("firstProducerWave").GetInt32()
                && item.GetProperty("dependencyRole").GetString() == "ProducedAndConsumed"
                && item.GetProperty("evidence").GetArrayLength() > 0);
        var artifactInventory = diagnostics.GetProperty("artifactInventory").EnumerateArray().ToArray();
        Assert.Equal(
            artifactInventory.Count(item => item.GetProperty("isPresent").GetBoolean()),
            diagnostics.GetProperty("availableArtifactCount").GetInt32());
        Assert.Contains(
            artifactInventory,
            item => item.GetProperty("artifact").GetString() == nameof(PlanArtifactKind.Walls)
                && item.GetProperty("count").GetInt32() >= 4
                && !string.IsNullOrWhiteSpace(item.GetProperty("stateKey").GetString())
                && item.GetProperty("revision").GetInt32() > 0
                && item.GetProperty("isImportCritical").GetBoolean()
                && item.GetProperty("readinessImpact").GetString() == "GeometryImport");
        var wallGraphStage = diagnostics.GetProperty("stages")
            .EnumerateArray()
            .First(stage => stage.GetProperty("stage").GetString() == "wall-graph");
        var wallGraphRuntimeReadiness = wallGraphStage.GetProperty("runtimeReadiness");
        Assert.True(wallGraphRuntimeReadiness.GetProperty("requiredReadsHaveData").GetBoolean());
        Assert.False(wallGraphRuntimeReadiness.GetProperty("hasEmptyRequiredReads").GetBoolean());
        Assert.Contains(
            nameof(PlanArtifactKind.Walls),
            wallGraphRuntimeReadiness.GetProperty("nonEmptyRequiredReads").EnumerateArray().Select(item => item.GetString()));
        Assert.Empty(wallGraphRuntimeReadiness.GetProperty("emptyRequiredReads").EnumerateArray());
        var wallGraphOutputReadiness = wallGraphStage.GetProperty("outputReadiness");
        Assert.True(wallGraphOutputReadiness.GetProperty("declaredOutputsHaveData").GetBoolean());
        Assert.False(wallGraphOutputReadiness.GetProperty("hasEmptyDeclaredOutputs").GetBoolean());
        Assert.Contains(
            nameof(PlanArtifactKind.WallGraph),
            wallGraphOutputReadiness.GetProperty("nonEmptyDeclaredOutputs").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            nameof(PlanArtifactKind.WallGraph),
            wallGraphOutputReadiness.GetProperty("changedDeclaredOutputs").EnumerateArray().Select(item => item.GetString()));
        Assert.Empty(wallGraphOutputReadiness.GetProperty("undeclaredChangedArtifacts").EnumerateArray());
        Assert.True(wallGraphOutputReadiness.GetProperty("evidence").GetArrayLength() > 0);
        Assert.Contains(
            wallGraphStage.GetProperty("inputArtifacts").EnumerateArray(),
            item => item.GetProperty("artifact").GetString() == nameof(PlanArtifactKind.Walls)
                && !string.IsNullOrWhiteSpace(item.GetProperty("stateKey").GetString())
                && item.GetProperty("revision").GetInt32() > 0
                && item.GetProperty("evidence").GetArrayLength() > 0);
        Assert.True(wallGraphRuntimeReadiness.GetProperty("evidence").GetArrayLength() > 0);
        Assert.Contains(
            wallGraphStage.GetProperty("artifactDeltas").EnumerateArray(),
            item => item.GetProperty("artifact").GetString() == nameof(PlanArtifactKind.WallGraph)
                && item.GetProperty("changeKind").GetString() == nameof(PipelineArtifactDeltaKind.Created)
                && item.GetProperty("isDeclaredWrite").GetBoolean()
                && item.GetProperty("changed").GetBoolean()
                && item.GetProperty("evidence").GetArrayLength() > 0);
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
        Assert.False(string.IsNullOrWhiteSpace(firstWall.GetProperty("wallType").GetString()));
        Assert.False(firstWall.GetProperty("wallComponentId").ValueKind == JsonValueKind.Null);
        Assert.Equal("MainStructural", firstWall.GetProperty("wallComponentKind").GetString());
        Assert.False(firstWall.GetProperty("excludedFromStructuralTopology").GetBoolean());
        var firstWallTopologySpan = Assert.Single(firstWall.GetProperty("topologySpans").EnumerateArray());
        Assert.Equal(firstWall.GetProperty("id").GetString(), firstWallTopologySpan.GetProperty("wallId").GetString());
        Assert.Equal(JsonValueKind.Object, firstWallTopologySpan.GetProperty("centerLine").ValueKind);
        Assert.True(firstWallTopologySpan.GetProperty("sourceWallStartOffsetDrawingUnits").GetDouble() >= 0);
        Assert.True(firstWallTopologySpan.GetProperty("sourceWallEndOffsetDrawingUnits").GetDouble() >= 0);
        Assert.Equal(
            firstWallTopologySpan.GetProperty("drawingLength").GetDouble(),
            firstWallTopologySpan.GetProperty("sourceWallProjectedLengthDrawingUnits").GetDouble(),
            precision: 3);
        Assert.InRange(firstWallTopologySpan.GetProperty("sourceWallStartParameter").GetDouble(), -0.001, 1.001);
        Assert.InRange(firstWallTopologySpan.GetProperty("sourceWallEndParameter").GetDouble(), -0.001, 1.001);
        Assert.True(firstWallTopologySpan.GetProperty("sourceWallStartProjectionDistanceDrawingUnits").GetDouble() <= 0.001);
        Assert.True(firstWallTopologySpan.GetProperty("sourceWallEndProjectionDistanceDrawingUnits").GetDouble() <= 0.001);
        Assert.True(firstWall.GetProperty("sourcePrimitiveIds").GetArrayLength() > 0);
        var firstWallEvidenceAssessment = firstWall.GetProperty("evidenceAssessment");
        Assert.False(string.IsNullOrWhiteSpace(firstWallEvidenceAssessment.GetProperty("category").GetString()));
        Assert.InRange(firstWallEvidenceAssessment.GetProperty("confidence").GetDouble(), 0, 1);
        Assert.True(firstWallEvidenceAssessment.GetProperty("placementReady").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(firstWallEvidenceAssessment.GetProperty("requiresReview").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(firstWallEvidenceAssessment.GetProperty("rejectedAsNoise").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(firstWallEvidenceAssessment.GetProperty("sourcePrimitiveIds").GetArrayLength() > 0);
        Assert.True(firstWallEvidenceAssessment.GetProperty("evidence").GetArrayLength() > 0);
        Assert.True(firstWall.GetProperty("evidence").GetArrayLength() > 0);
        Assert.Contains("Wall", firstWall.GetProperty("sourceLayers").EnumerateArray().Select(layer => layer.GetString()));

        var firstWallEdge = document.RootElement.GetProperty("wallGraph").GetProperty("edges")[0];
        Assert.Equal(JsonValueKind.Object, firstWallEdge.GetProperty("line").ValueKind);
        Assert.Equal(JsonValueKind.Object, firstWallEdge.GetProperty("lineMillimeters").ValueKind);
        Assert.Equal(JsonValueKind.Object, firstWallEdge.GetProperty("bounds").ValueKind);
        Assert.Equal(JsonValueKind.Object, firstWallEdge.GetProperty("boundsMillimeters").ValueKind);
        Assert.True(firstWallEdge.GetProperty("drawingLength").GetDouble() > 0);
        Assert.True(firstWallEdge.GetProperty("lengthMeters").GetDouble() > 0);
        Assert.True(firstWallEdge.GetProperty("thicknessDrawingUnits").GetDouble() > 0);
        Assert.True(firstWallEdge.GetProperty("thicknessMillimeters").GetDouble() > 0);
        Assert.True(firstWallEdge.GetProperty("millimetersPerDrawingUnit").GetDouble() > 0);
        Assert.Contains("Wall", firstWallEdge.GetProperty("sourceLayers").EnumerateArray().Select(layer => layer.GetString()));
    }

    [Fact]
    public async Task CompactJsonExporter_WritesLosslessDictionaryEncodedScanResult()
    {
        var result = await CreateScanResultAsync();
        var export = PlanTraceExport.From(result);
        var fullJson = JsonSerializer.Serialize(
            export,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });

        var compactJson = PlanTraceCompactJsonExporter.Serialize(export);
        var expandedJson = PlanTraceCompactJsonExporter.ExpandToScanJson(compactJson);

        using var compactDocument = JsonDocument.Parse(compactJson);
        var root = compactDocument.RootElement;
        Assert.Equal(PlanTraceCompactJsonExporter.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetString());
        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, root.GetProperty("sourceSchemaVersion").GetString());
        Assert.Equal("shape-string-token-v1", root.GetProperty("encoding").GetString());

        var dictionary = root.GetProperty("dictionary");
        Assert.True(dictionary.GetProperty("strings").GetArrayLength() > 0);
        Assert.True(dictionary.GetProperty("shapes").GetArrayLength() > 0);
        Assert.True(root.GetProperty("stats").GetProperty("encodedObjectCount").GetInt32() > 0);
        Assert.True(root.GetProperty("stats").GetProperty("encodedStringReferenceCount").GetInt32() > 0);

        var fullBytes = Encoding.UTF8.GetByteCount(fullJson);
        var compactBytes = Encoding.UTF8.GetByteCount(compactJson);
        Assert.Equal(fullBytes, root.GetProperty("stats").GetProperty("sourceUtf8Bytes").GetInt32());
        Assert.True(
            compactBytes < fullBytes,
            $"Expected compact scan JSON to be smaller than minified scan JSON. compact={compactBytes}, full={fullBytes}");

        var fullNode = JsonNode.Parse(fullJson);
        var expandedNode = JsonNode.Parse(expandedJson);
        Assert.True(JsonNode.DeepEquals(fullNode, expandedNode));
    }

    [Fact]
    public async Task JsonExporter_WritesWallEvidenceMapAndRejectedWallLikeDetails()
    {
        var result = await CreateScanResultAsync();
        var rejectedAssessment = new WallEvidenceWallAssessment(
            "wall-door-leaf-noise",
            1,
            new PlanRect(196, 96, 8, 40),
            WallEvidenceCategory.DoorOrOpeningSymbol,
            Confidence.High,
            PlacementReady: false,
            RequiresReview: true,
            RejectedAsNoise: true,
            new[] { "wall-top" },
            new[] { "wall evidence: rejected as door/opening leaf linework radially tied to swing arc door-swing" })
        {
            Decision = WallEvidenceDecision.Reject,
            ScoreBreakdown = new WallEvidenceScoreBreakdown(
                PositiveScore: 0.1,
                NegativeScore: 0.9,
                DecisionScore: -0.8,
                PairSupportScore: 0,
                LayerSupportScore: 0,
                StructuralSupportScore: 0.1,
                RecoverySupportScore: 0,
                NoisePenalty: 0.9,
                FragmentReviewPenalty: 0,
                PositiveEvidence: new[] { "one endpoint supported by structural context" },
                NegativeEvidence: new[] { "explicit non-wall evidence: DoorOrOpeningSymbol" })
        };
        var rejectedSegment = new WallEvidenceSegment(
            "wall-evidence-segment:wall-door-leaf-noise",
            1,
            new PlanLineSegment(new PlanPoint(200, 100), new PlanPoint(200, 132)),
            new PlanRect(196, 96, 8, 40),
            WallEvidenceCategory.DoorOrOpeningSymbol,
            Confidence.High,
            "wall-door-leaf-noise",
            new[] { "wall-top" },
            rejectedAssessment.Evidence);
        result = result with
        {
            WallEvidenceMap = new WallEvidenceMap(
                new[] { rejectedSegment },
                Array.Empty<WallEvidenceBand>(),
                new[] { rejectedAssessment },
                SourceCandidateWallCount: 3,
                RecoveredCandidateWallCount: 1)
        };

        var json = PlanTraceJsonExporter.Serialize(result);
        using var document = JsonDocument.Parse(json);
        var wallEvidence = document.RootElement.GetProperty("wallEvidence");

        Assert.Equal(1, wallEvidence.GetProperty("segmentCount").GetInt32());
        Assert.Equal(1, wallEvidence.GetProperty("wallAssessmentCount").GetInt32());
        Assert.Equal(3, wallEvidence.GetProperty("sourceCandidateWallCount").GetInt32());
        Assert.Equal(1, wallEvidence.GetProperty("recoveredCandidateWallCount").GetInt32());
        Assert.Equal(4, wallEvidence.GetProperty("totalCandidateWallCount").GetInt32());
        Assert.Equal(1, wallEvidence.GetProperty("rejectedNoiseCount").GetInt32());
        Assert.Equal(1, wallEvidence.GetProperty("rejectedDoorOrOpeningSymbolCount").GetInt32());
        Assert.Equal(0, wallEvidence.GetProperty("rejectedSurfacePatternDetailCount").GetInt32());
        Assert.Equal(0, wallEvidence.GetProperty("rejectedDimensionOrAnnotationCount").GetInt32());
        Assert.Equal(0, wallEvidence.GetProperty("rejectedObjectOrFixtureDetailCount").GetInt32());
        Assert.Contains("wall-door-leaf-noise", JsonStrings(wallEvidence.GetProperty("rejectedDoorOrOpeningSymbolWallIds")));
        Assert.Empty(wallEvidence.GetProperty("rejectedSurfacePatternDetailWallIds").EnumerateArray());
        Assert.Empty(wallEvidence.GetProperty("rejectedDimensionOrAnnotationWallIds").EnumerateArray());
        Assert.Empty(wallEvidence.GetProperty("rejectedObjectOrFixtureDetailWallIds").EnumerateArray());
        Assert.Equal(0, wallEvidence.GetProperty("acceptedWallCount").GetInt32());
        Assert.Equal(0, wallEvidence.GetProperty("reviewDecisionWallCount").GetInt32());
        Assert.Equal(1, wallEvidence.GetProperty("rejectedWallCount").GetInt32());
        Assert.Equal(0, wallEvidence.GetProperty("placementReadyWallCount").GetInt32());
        Assert.Equal(1, wallEvidence.GetProperty("reviewWallCount").GetInt32());
        Assert.Empty(wallEvidence.GetProperty("acceptedWallIds").EnumerateArray());
        Assert.Empty(wallEvidence.GetProperty("reviewWallIds").EnumerateArray());
        Assert.Contains("wall-door-leaf-noise", JsonStrings(wallEvidence.GetProperty("rejectedWallIds")));

        var segment = Assert.Single(wallEvidence.GetProperty("segments").EnumerateArray());
        Assert.Equal("wall-door-leaf-noise", segment.GetProperty("wallId").GetString());
        Assert.Equal("DoorOrOpeningSymbol", segment.GetProperty("category").GetString());
        Assert.Equal("Reject", segment.GetProperty("decision").GetString());
        Assert.Equal(-0.8, segment.GetProperty("scoreBreakdown").GetProperty("decisionScore").GetDouble(), precision: 3);
        Assert.True(segment.GetProperty("rejectedAsNoise").GetBoolean());
        Assert.Equal(JsonValueKind.Object, segment.GetProperty("line").ValueKind);
        Assert.Equal(JsonValueKind.Object, segment.GetProperty("bounds").ValueKind);
        Assert.Contains("Wall", JsonStrings(segment.GetProperty("sourceLayers")));

        var assessment = Assert.Single(wallEvidence.GetProperty("wallAssessments").EnumerateArray());
        Assert.Equal("Reject", assessment.GetProperty("decision").GetString());
        Assert.Equal(0.9, assessment.GetProperty("scoreBreakdown").GetProperty("noisePenalty").GetDouble(), precision: 3);
        Assert.Contains(
            JsonStrings(assessment.GetProperty("scoreBreakdown").GetProperty("negativeEvidence")),
            item => item.Contains("DoorOrOpeningSymbol", StringComparison.OrdinalIgnoreCase));

        var rejected = Assert.Single(wallEvidence.GetProperty("rejectedWallLikeDetails").EnumerateArray());
        Assert.Equal("wall-door-leaf-noise", rejected.GetProperty("wallId").GetString());
        Assert.Equal("DoorOrOpeningSymbol", rejected.GetProperty("category").GetString());
        Assert.Equal("Reject", rejected.GetProperty("decision").GetString());
        Assert.Equal(0.1, rejected.GetProperty("scoreBreakdown").GetProperty("structuralSupportScore").GetDouble(), precision: 3);
        Assert.Equal(JsonValueKind.Object, rejected.GetProperty("bounds").ValueKind);
        Assert.Equal(JsonValueKind.Object, rejected.GetProperty("centerLine").ValueKind);
        Assert.Contains("wall-evidence-segment:wall-door-leaf-noise", JsonStrings(rejected.GetProperty("segmentIds")));
        Assert.Contains("wall-top", JsonStrings(rejected.GetProperty("sourcePrimitiveIds")));
        Assert.Contains("Wall", JsonStrings(rejected.GetProperty("sourceLayers")));
        Assert.Contains(
            JsonStrings(rejected.GetProperty("evidence")),
            item => item.Contains("radially tied to swing arc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task JsonExporter_WritesWallTopologyPreparationTrustBuckets()
    {
        var result = await CreateScanResultAsync();
        var acceptedWallId = result.Walls[0].Id;
        var reviewWallId = result.Walls[1].Id;
        var unassessedWallId = result.Walls[2].Id;
        result = result with
        {
            WallTopologyPreparation = new WallTopologyPreparation(
                new[] { acceptedWallId, reviewWallId, unassessedWallId },
                new[]
                {
                    new WallTopologyRejectedWall(
                        "wall-door-leaf-noise",
                        1,
                        new PlanRect(196, 96, 8, 40),
                        WallEvidenceCategory.DoorOrOpeningSymbol,
                        WallEvidenceDecision.Reject,
                        RejectedAsNoise: true,
                        new[] { "wall-top" },
                        new[] { "wall topology preparation: rejected door/opening detail excluded from graph input" })
                },
                new[] { acceptedWallId },
                new[] { reviewWallId },
                new[] { unassessedWallId })
        };

        var json = PlanTraceJsonExporter.Serialize(result);
        using var document = JsonDocument.Parse(json);
        var preparation = document.RootElement.GetProperty("wallTopologyPreparation");

        Assert.Equal(3, preparation.GetProperty("graphWallCount").GetInt32());
        Assert.Equal(1, preparation.GetProperty("acceptedGraphWallCount").GetInt32());
        Assert.Equal(1, preparation.GetProperty("reviewGraphWallCount").GetInt32());
        Assert.Equal(1, preparation.GetProperty("unassessedGraphWallCount").GetInt32());
        Assert.Equal(2, preparation.GetProperty("automaticCoordinateRepairWallCount").GetInt32());
        Assert.Equal(1, preparation.GetProperty("rejectedWallCount").GetInt32());
        Assert.Equal(1, preparation.GetProperty("rejectedAssessmentCount").GetInt32());
        Assert.Equal(1, preparation.GetProperty("doorOrOpeningSymbolCount").GetInt32());
        Assert.Equal(0, preparation.GetProperty("surfacePatternDetailCount").GetInt32());
        Assert.Contains(acceptedWallId, JsonStrings(preparation.GetProperty("graphWallIds")));
        Assert.Contains(acceptedWallId, JsonStrings(preparation.GetProperty("acceptedGraphWallIds")));
        Assert.Contains(reviewWallId, JsonStrings(preparation.GetProperty("reviewGraphWallIds")));
        Assert.Contains(unassessedWallId, JsonStrings(preparation.GetProperty("unassessedGraphWallIds")));
        Assert.Contains(acceptedWallId, JsonStrings(preparation.GetProperty("automaticCoordinateRepairWallIds")));
        Assert.Contains(unassessedWallId, JsonStrings(preparation.GetProperty("automaticCoordinateRepairWallIds")));
        Assert.DoesNotContain(reviewWallId, JsonStrings(preparation.GetProperty("automaticCoordinateRepairWallIds")));
        Assert.Contains("wall-door-leaf-noise", JsonStrings(preparation.GetProperty("rejectedWallIds")));

        var rejected = Assert.Single(preparation.GetProperty("rejectedWalls").EnumerateArray());
        Assert.Equal("wall-door-leaf-noise", rejected.GetProperty("wallId").GetString());
        Assert.Equal("DoorOrOpeningSymbol", rejected.GetProperty("category").GetString());
        Assert.Equal("Reject", rejected.GetProperty("decision").GetString());
        Assert.True(rejected.GetProperty("rejectedAsNoise").GetBoolean());
        Assert.Contains("wall-top", JsonStrings(rejected.GetProperty("sourcePrimitiveIds")));
        Assert.Contains("Wall", JsonStrings(rejected.GetProperty("sourceLayers")));
        Assert.Contains(
            JsonStrings(rejected.GetProperty("evidence")),
            item => item.Contains("excluded from graph input", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            JsonStrings(preparation.GetProperty("evidence")),
            item => item.Contains("automatic coordinate repair allowed walls: 2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Exporters_MarkRejectedWallEvidenceAsTopologyExcluded()
    {
        var result = await CreateScanResultAsync();
        var rejectedEdge = result.WallGraph.Edges[0];
        var rejectedWall = result.Walls.Single(wall => wall.Id == rejectedEdge.WallId);
        var rejectedComponent = result.WallGraph.Components.Single(component => component.WallIds.Contains(rejectedWall.Id));
        var rejectedAssessment = new WallEvidenceWallAssessment(
            rejectedWall.Id,
            rejectedWall.PageNumber,
            rejectedWall.Bounds,
            WallEvidenceCategory.DoorOrOpeningSymbol,
            Confidence.High,
            PlacementReady: false,
            RequiresReview: true,
            RejectedAsNoise: true,
            rejectedWall.SourcePrimitiveIds,
            new[] { "wall evidence: rejected as door/opening symbol" })
        {
            Decision = WallEvidenceDecision.Reject
        };
        result = result with
        {
            WallEvidenceMap = new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                new[] { rejectedAssessment })
        };

        using var scanDocument = JsonDocument.Parse(PlanTraceJsonExporter.Serialize(result));
        var scanWall = scanDocument.RootElement
            .GetProperty("walls")
            .EnumerateArray()
            .Single(wall => wall.GetProperty("id").GetString() == rejectedWall.Id);

        Assert.True(scanWall.GetProperty("excludedFromStructuralTopology").GetBoolean());
        Assert.True(scanWall.GetProperty("evidenceAssessment").GetProperty("rejectedAsNoise").GetBoolean());
        var scanGraphEdge = scanDocument.RootElement
            .GetProperty("wallGraph")
            .GetProperty("edges")
            .EnumerateArray()
            .Single(edge => edge.GetProperty("id").GetString() == rejectedEdge.Id);

        Assert.Equal(rejectedComponent.Id, scanGraphEdge.GetProperty("wallComponentId").GetString());
        Assert.Equal(rejectedComponent.Kind.ToString(), scanGraphEdge.GetProperty("wallComponentKind").GetString());
        Assert.True(scanGraphEdge.GetProperty("excludedFromStructuralTopology").GetBoolean());
        Assert.Contains(
            scanGraphEdge.GetProperty("evidence").EnumerateArray().Select(item => item.GetString()),
            item => item is not null && item.Contains("wall evidence rejected as non-wall/noise", StringComparison.OrdinalIgnoreCase));

        using var placementDocument = JsonDocument.Parse(
            PlanPlacementJsonExporter.Serialize(
                result,
                new PlanPlacementJsonExportOptions { WriteIndented = false }));
        var placementWall = placementDocument.RootElement
            .GetProperty("walls")
            .EnumerateArray()
            .Single(wall => wall.GetProperty("id").GetString() == rejectedWall.Id);

        Assert.True(placementWall.GetProperty("excludedFromStructuralTopology").GetBoolean());
        Assert.True(placementWall.GetProperty("evidenceAssessment").GetProperty("rejectedAsNoise").GetBoolean());
        var placementWallGraph = placementDocument.RootElement.GetProperty("wallGraph");
        var placementGraphEdge = placementWallGraph
            .GetProperty("edges")
            .EnumerateArray()
            .Single(edge => edge.GetProperty("id").GetString() == rejectedEdge.Id);

        Assert.True(placementGraphEdge.GetProperty("excludedFromStructuralTopology").GetBoolean());
        Assert.Equal(
            1,
            placementWallGraph.GetProperty("summary").GetProperty("excludedEdgeCount").GetInt32());
        Assert.Contains(
            placementGraphEdge.GetProperty("evidence").EnumerateArray().Select(item => item.GetString()),
            item => item is not null && item.Contains("wall evidence rejected as non-wall/noise", StringComparison.OrdinalIgnoreCase));

        using var geoJsonDocument = JsonDocument.Parse(
            PlanTraceGeoJsonExporter.Serialize(
                result,
                new PlanTraceGeoJsonExportOptions { WriteIndented = false }));
        var geoJsonWall = geoJsonDocument.RootElement
            .GetProperty("features")
            .EnumerateArray()
            .Single(feature =>
                FeatureType(feature) == "wall"
                && feature.GetProperty("properties").GetProperty("openPlanTraceId").GetString() == rejectedWall.Id);

        Assert.True(geoJsonWall.GetProperty("properties").GetProperty("excludedFromStructuralTopology").GetBoolean());
        Assert.True(geoJsonWall.GetProperty("properties").GetProperty("wallEvidenceRejectedAsNoise").GetBoolean());
    }

    [Fact]
    public void WallEvidenceMap_ExposesStableDecisionBuckets()
    {
        var evidenceMap = new WallEvidenceMap(
            Array.Empty<WallEvidenceSegment>(),
            Array.Empty<WallEvidenceBand>(),
            new[]
            {
                Assessment("wall-z", WallEvidenceDecision.Accept),
                Assessment("wall-a", WallEvidenceDecision.Accept),
                Assessment("wall-a", WallEvidenceDecision.Accept),
                Assessment("wall-review", WallEvidenceDecision.Review),
                Assessment("wall-reject", WallEvidenceDecision.Reject, WallEvidenceCategory.DoorOrOpeningSymbol),
                Assessment("wall-surface-pattern", WallEvidenceDecision.Reject, WallEvidenceCategory.SurfacePatternDetail),
                Assessment("wall-dimension", WallEvidenceDecision.Reject, WallEvidenceCategory.DimensionOrAnnotation),
                Assessment("wall-object", WallEvidenceDecision.Reject, WallEvidenceCategory.ObjectOrFixtureDetail)
            },
            SourceCandidateWallCount: 7,
            RecoveredCandidateWallCount: 2);

        Assert.Equal(7, evidenceMap.SourceCandidateWallCount);
        Assert.Equal(2, evidenceMap.RecoveredCandidateWallCount);
        Assert.Equal(9, evidenceMap.TotalCandidateWallCount);
        Assert.Equal(3, evidenceMap.AcceptedWallCount);
        Assert.Equal(1, evidenceMap.ReviewDecisionWallCount);
        Assert.Equal(4, evidenceMap.RejectedWallCount);
        Assert.Equal(1, evidenceMap.RejectedDoorOrOpeningSymbolCount);
        Assert.Equal(1, evidenceMap.RejectedSurfacePatternDetailCount);
        Assert.Equal(1, evidenceMap.RejectedDimensionOrAnnotationCount);
        Assert.Equal(1, evidenceMap.RejectedObjectOrFixtureDetailCount);
        Assert.Equal(new[] { "wall-reject" }, evidenceMap.RejectedDoorOrOpeningSymbolWallIds);
        Assert.Equal(new[] { "wall-surface-pattern" }, evidenceMap.RejectedSurfacePatternDetailWallIds);
        Assert.Equal(new[] { "wall-dimension" }, evidenceMap.RejectedDimensionOrAnnotationWallIds);
        Assert.Equal(new[] { "wall-object" }, evidenceMap.RejectedObjectOrFixtureDetailWallIds);
        Assert.Equal(new[] { "wall-a", "wall-z" }, evidenceMap.AcceptedWallIds);
        Assert.Equal(new[] { "wall-review" }, evidenceMap.ReviewWallIds);
        Assert.Equal(
            new[] { "wall-dimension", "wall-object", "wall-reject", "wall-surface-pattern" },
            evidenceMap.RejectedWallIds);

        static WallEvidenceWallAssessment Assessment(
            string wallId,
            WallEvidenceDecision decision,
            WallEvidenceCategory category = WallEvidenceCategory.StrongWallBody) =>
            new(
                wallId,
                1,
                new PlanRect(0, 0, 10, 10),
                category,
                Confidence.High,
                PlacementReady: decision == WallEvidenceDecision.Accept,
                RequiresReview: decision == WallEvidenceDecision.Review,
                RejectedAsNoise: decision == WallEvidenceDecision.Reject,
                new[] { $"{wallId}-primitive" },
                new[] { "test wall evidence" })
            {
                Decision = decision
            };
    }

    [Fact]
    public async Task JsonExporter_ReportsDwgAdapterBackedSourceReadiness()
    {
        var result = await CreateScanResultAsync();
        result = result with
        {
            Document = result.Document with
            {
                Metadata = result.Document.Metadata with
                {
                    Properties = new Dictionary<string, string>
                    {
                        ["format"] = "dwg",
                        ["loader"] = "DWG-to-DXF/TestConverter",
                        ["sourceKind"] = PlanSourceKind.Dwg.ToString(),
                        ["effectiveSourceKind"] = PlanSourceKind.Dwg.ToString(),
                        ["dwg.conversion"] = "dwg-to-dxf",
                        ["dwg.converter"] = "TestConverter",
                        ["dwg.intermediateFormat"] = "dxf",
                        ["dwg.intermediateLoader"] = "DXF/IxMilia"
                    }
                }
            }
        };

        using var document = JsonDocument.Parse(PlanTraceJsonExporter.Serialize(result));
        var source = document.RootElement.GetProperty("document");
        var readiness = source.GetProperty("sourceReadiness");

        Assert.True(source.GetProperty("isDwgDerived").GetBoolean());
        Assert.Equal("dwg-to-dxf", source.GetProperty("ingestionPath").GetString());
        Assert.Equal("DwgAdapterBacked", readiness.GetProperty("status").GetString());
        Assert.Equal("converted-dxf-vector-geometry", readiness.GetProperty("geometryBasis").GetString());
        Assert.True(readiness.GetProperty("canUseVectorGeometry").GetBoolean());
        Assert.True(readiness.GetProperty("requiresExternalAdapter").GetBoolean());
        Assert.False(readiness.GetProperty("requiresOcr").GetBoolean());
        Assert.True(readiness.GetProperty("isLegalAdapterBacked").GetBoolean());
        Assert.Contains(
            "dwg.converter=TestConverter",
            readiness.GetProperty("evidence").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            readiness.GetProperty("messages").EnumerateArray().Select(item => item.GetString()),
            message => message is not null && message.Contains("did not parse DWG natively", StringComparison.OrdinalIgnoreCase));
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
        Assert.Contains("r=\"0.95\"", svg);
        Assert.Contains("diagnostic-bg", svg);
        Assert.Contains("id=\"rooms\"", svg);
        Assert.Contains("id=\"room-adjacency\"", svg);
        Assert.Contains("id=\"annotation-references\"", svg);
        Assert.Contains("annotation-reference", svg);
        Assert.Contains("page:1:wall:", svg);
        Assert.Contains("viewBox=\"0 0 768", svg);
        Assert.Contains("id=\"legend\" transform=\"translate(516 12)\"", svg);
        Assert.DoesNotContain("id=\"legend\" transform=\"translate(12 12)\"", svg);
    }

    [Fact]
    public async Task SvgRenderer_EmbedsBackgroundImageForAlignmentQa()
    {
        var result = await CreateScanResultAsync();

        var svg = PlanOverlaySvgRenderer.RenderPage(
            result,
            1,
            SvgOverlayRenderOptions.ForProfile(SvgOverlayRenderProfile.PlacementReview) with
            {
                BackgroundImageHref = "../backgrounds/page-1.png",
                BackgroundImageOpacity = 1.25
            });

        Assert.Contains("class=\"sheet-image\"", svg);
        Assert.Contains("href=\"../backgrounds/page-1.png\"", svg);
        Assert.Contains("width=\"500\" height=\"400\"", svg);
        Assert.Contains("preserveAspectRatio=\"none\"", svg);
        Assert.Contains("opacity=\"1\"", svg);
        Assert.Contains("Source PDF page background for alignment QA", svg);
    }

    [Fact]
    public void SvgRenderer_RegularizesNearlyCollinearPlacementRuns()
    {
        var result = CreateJitteredPlacementRunResult();

        var svg = PlanOverlaySvgRenderer.RenderPage(
            result,
            1,
            SvgOverlayRenderOptions.ForProfile(SvgOverlayRenderProfile.PlacementReview));

        Assert.Contains("x1=\"100.307\"", svg);
        Assert.DoesNotContain("x1=\"102\"", svg);
        Assert.DoesNotContain("x1=\"98.8\"", svg);
    }

    [Fact]
    public void PlacementExporter_UsesRegularizedCleanPlacementRuns()
    {
        var result = CreateJitteredPlacementRunResult();

        using var document = JsonDocument.Parse(PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false }));

        var xCoordinates = document.RootElement
            .GetProperty("walls")
            .EnumerateArray()
            .SelectMany(wall => wall.GetProperty("topologySpans").EnumerateArray())
            .Select(span => span
                .GetProperty("centerLine")
                .GetProperty("start")
                .GetProperty("x")
                .GetDouble())
            .Distinct()
            .ToArray();

        var evidence = document.RootElement
            .GetProperty("walls")
            .EnumerateArray()
            .SelectMany(wall => wall.GetProperty("topologySpans").EnumerateArray())
            .SelectMany(span => span.GetProperty("evidence").EnumerateArray())
            .Select(item => item.GetString())
            .Where(item => item is not null)
            .ToArray();

        Assert.Single(xCoordinates);
        Assert.Contains(evidence, item => item!.Contains("clean placement regularization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SvgRenderer_DrawsWallBodyFootprintsWithAnchoredOpeningCutouts()
    {
        var result = CreateOpeningCutoutPlacementReviewResult();

        var svg = PlanOverlaySvgRenderer.RenderPage(
            result,
            1,
            SvgOverlayRenderOptions.ForProfile(SvgOverlayRenderProfile.PlacementReview));

        Assert.Contains("2 wall body footprints", svg);
        Assert.Contains("cutout-wall:solid-span:1:body-footprint", svg);
        Assert.Contains("cutout-wall:solid-span:2:body-footprint", svg);
        Assert.DoesNotContain("cutout-wall:solid-span:3:body-footprint", svg);
        Assert.Contains("body from placement solid span with anchored opening cutouts", svg);
    }

    [Fact]
    public void ScanArguments_ParseSvgBackgroundQaOptions()
    {
        var parsed = global::ScanArguments.Parse(new[]
        {
            "plan.pdf",
            "--svg-background-dir",
            "backgrounds",
            "--svg-background-opacity",
            "0.42"
        });

        Assert.Equal("plan.pdf", parsed.InputPath);
        Assert.Equal("backgrounds", parsed.SvgBackgroundImageDirectory);
        Assert.Equal(0.42, parsed.SvgBackgroundImageOpacity);
    }

    [Fact]
    public async Task SvgRenderer_MarksRejectedWallEvidenceAsTopologyExcluded()
    {
        var result = await CreateScanResultAsync();
        var rejectedWall = result.Walls[0];
        var rejectedAssessment = new WallEvidenceWallAssessment(
            rejectedWall.Id,
            rejectedWall.PageNumber,
            rejectedWall.Bounds,
            WallEvidenceCategory.DoorOrOpeningSymbol,
            Confidence.High,
            PlacementReady: false,
            RequiresReview: true,
            RejectedAsNoise: true,
            rejectedWall.SourcePrimitiveIds,
            new[] { "wall evidence: rejected as door/opening symbol" })
        {
            Decision = WallEvidenceDecision.Reject
        };
        result = result with
        {
            WallEvidenceMap = new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                new[] { rejectedAssessment })
        };

        var svg = PlanOverlaySvgRenderer.RenderPage(result, 1);

        Assert.Contains("wall-excluded", svg);
        Assert.Contains("topology excluded True", svg);
        Assert.Contains("wall evidence Reject DoorOrOpeningSymbol", svg);
        Assert.Contains("1 topology-excluded walls", svg);
    }

    [Fact]
    public async Task SvgRenderer_StructuralReviewProfileSuppressesNoisyDetailLayers()
    {
        var result = await CreateScanResultAsync();

        var svg = PlanOverlaySvgRenderer.RenderPage(
            result,
            1,
            SvgOverlayRenderOptions.ForProfile(SvgOverlayRenderProfile.StructuralReview));

        Assert.Contains("data-profile=\"structural-review\"", svg);
        Assert.Contains("id=\"walls\"", svg);
        Assert.Contains("id=\"rooms\"", svg);
        Assert.Contains("id=\"openings\"", svg);
        Assert.DoesNotContain("id=\"objects\"", svg);
        Assert.DoesNotContain("id=\"object-aggregates\"", svg);
        Assert.DoesNotContain("id=\"wall-components\"", svg);
        Assert.DoesNotContain("id=\"wall-nodes\"", svg);
        Assert.DoesNotContain("id=\"room-adjacency\"", svg);
    }

    [Fact]
    public void SvgRenderer_DrawsRawWallLinesInsteadOfTopologySpans()
    {
        var result = CreateDenseMinorRoutingDetailResult();

        var svg = PlanOverlaySvgRenderer.RenderPage(result, 1);

        Assert.Contains("detail-host", svg);
        Assert.Contains("x1=\"100\" y1=\"100\" x2=\"300\" y2=\"100\"", svg);
        Assert.DoesNotContain("detail-host span edge-host-1", svg);
    }

    [Fact]
    public void SvgRenderer_PlacementReviewProfileDrawsCleanTopologySpansWithoutRawWallLayer()
    {
        var result = WithReviewOnlyWallAssessment(
            CreateDenseMinorRoutingDetailResult(),
            "detail-tooth-1");

        var svg = PlanOverlaySvgRenderer.RenderPage(
            result,
            1,
            SvgOverlayRenderOptions.ForProfile(SvgOverlayRenderProfile.PlacementReview));

        Assert.Contains("data-profile=\"placement-review\"", svg);
        Assert.Contains("id=\"wall-body-footprints\"", svg);
        Assert.Contains("id=\"wall-topology-spans\"", svg);
        Assert.Contains("wall body footprint detail-host:solid-span:1:body-footprint", svg);
        Assert.Contains("body from placement solid span with anchored opening cutouts", svg);
        Assert.Contains("clean wall topology span detail-host:clean-run:1", svg);
        Assert.Contains("x1=\"100\" y1=\"100\" x2=\"300\" y2=\"100\"", svg);
        Assert.Contains("1 placement-ready walls", svg);
        Assert.Contains("4 omitted/review walls", svg);
        Assert.Contains("1 wall body footprints", svg);
        Assert.Contains("1 visible topology spans", svg);
        Assert.Contains("4 hidden non-placement topology spans", svg);
        Assert.DoesNotContain("edge-tooth-1", svg);
        Assert.DoesNotContain("edge-tooth-2", svg);
        Assert.DoesNotContain("id=\"walls\"", svg);
    }

    [Fact]
    public void VisualSnapshotExporter_PlacementReviewProfileExposesWallTopologySpanLayer()
    {
        var result = WithReviewOnlyWallAssessment(
            CreateDenseMinorRoutingDetailResult(),
            "detail-tooth-1");

        var snapshot = PlanOverlaySnapshot.From(
            result,
            new Dictionary<int, string> { [1] = "overlays/page-1.svg" },
            SvgOverlayRenderOptions.ForProfile(SvgOverlayRenderProfile.PlacementReview));

        var page = Assert.Single(snapshot.Pages);
        Assert.Equal("placement-review", page.SvgProfile);
        Assert.Contains("wallBodyFootprints", page.VisibleLayerNames);
        Assert.Contains("wallTopologySpans", page.VisibleLayerNames);
        Assert.DoesNotContain("walls", page.VisibleLayerNames);
        Assert.Equal(1, page.Layers.Single(layer => layer.Name == "wallBodyFootprints").Count);
        Assert.Equal(1, page.Layers.Single(layer => layer.Name == "wallTopologySpans").Count);
        Assert.Equal(4, page.Layers.Single(layer => layer.Name == "wallTopologyReviewSpans").Count);
        Assert.Contains("wallTopologyReviewSpans", page.HiddenLayerNames);
    }

    [Fact]
    public void SvgRenderer_PlacementReviewProfileKeepsIsolatedFragmentsOutOfCleanTopologySpans()
    {
        var result = WithPlacementReadyIsolatedFragment(CreateDenseMinorRoutingDetailResult());

        var svg = PlanOverlaySvgRenderer.RenderPage(
            result,
            1,
            SvgOverlayRenderOptions.ForProfile(SvgOverlayRenderProfile.PlacementReview));

        Assert.Contains("1 placement-ready walls", svg);
        Assert.Contains("5 omitted/review walls", svg);
        Assert.Contains("1 visible topology spans", svg);
        Assert.Contains("1 wall body footprints", svg);
        Assert.Contains("5 hidden non-placement topology spans", svg);
        Assert.DoesNotContain("clean wall topology span isolated-clean-fragment:clean-run:1", svg);

        var snapshot = PlanOverlaySnapshot.From(
            result,
            new Dictionary<int, string> { [1] = "overlays/page-1.svg" },
            SvgOverlayRenderOptions.ForProfile(SvgOverlayRenderProfile.PlacementReview));

        var page = Assert.Single(snapshot.Pages);
        Assert.Equal(1, page.Layers.Single(layer => layer.Name == "wallBodyFootprints").Count);
        Assert.Equal(1, page.Layers.Single(layer => layer.Name == "wallTopologySpans").Count);
        Assert.Equal(5, page.Layers.Single(layer => layer.Name == "wallTopologyReviewSpans").Count);
    }

    [Fact]
    public void SvgRenderer_PlacementReviewProfileDrawsWallGraphRepairCandidatesAsSeparateQaLayer()
    {
        var result = WithWallGraphRepairCandidate(CreateDenseMinorRoutingDetailResult());

        var svg = PlanOverlaySvgRenderer.RenderPage(
            result,
            1,
            SvgOverlayRenderOptions.ForProfile(SvgOverlayRenderProfile.PlacementReview));

        Assert.Contains("id=\"wall-graph-repairs\"", svg);
        Assert.Contains("EndpointToWall SnapEndpointToWall", svg);
        Assert.Contains("1 wall graph repairs (1 blocking)", svg);
        Assert.Contains("class=\"wall-graph-repair wall-graph-repair-high\"", svg);
        Assert.DoesNotContain("clean wall topology span detail-host", svg);
        Assert.DoesNotContain("wall body footprint detail-host", svg);

        var snapshot = PlanOverlaySnapshot.From(
            result,
            new Dictionary<int, string> { [1] = "overlays/page-1.svg" },
            SvgOverlayRenderOptions.ForProfile(SvgOverlayRenderProfile.PlacementReview));

        var page = Assert.Single(snapshot.Pages);
        Assert.Contains("wallGraphRepairs", page.VisibleLayerNames);
        var repairLayer = page.Layers.Single(layer => layer.Name == "wallGraphRepairs");
        Assert.Equal(1, repairLayer.Count);
        Assert.Equal(1, repairLayer.Breakdown["High"]);
        Assert.True(page.Layers.Single(layer => layer.Name == "wallTopologyReviewSpans").Count > 0);
    }

    [Fact]
    public void PlacementJsonExporter_BlocksWallsWithTopologyImportBlockedRepairCandidates()
    {
        var result = WithWallGraphRepairCandidate(CreateDenseMinorRoutingDetailResult());

        using var parsed = JsonDocument.Parse(PlanPlacementJsonExporter.Serialize(result));
        var root = parsed.RootElement;
        var candidate = Assert.Single(root.GetProperty("wallGraphRepairCandidates").EnumerateArray());
        var wall = Assert.Single(root.GetProperty("walls").EnumerateArray(), item =>
            item.GetProperty("id").GetString() == "detail-host");

        Assert.Equal("TopologyImportBlocked", candidate.GetProperty("importImpact").GetString());
        Assert.Contains(
            candidate.GetProperty("id").GetString(),
            wall.GetProperty("wallGraphRepairCandidateIds").EnumerateArray().Select(item => item.GetString()));
        Assert.False(wall.GetProperty("reliability").GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.True(wall.GetProperty("reliability").GetProperty("requiresReview").GetBoolean());
        Assert.Contains(
            wall.GetProperty("reliability").GetProperty("reasons").EnumerateArray().Select(item => item.GetString()),
            reason => reason is not null
                && reason.Contains(candidate.GetProperty("id").GetString()!, StringComparison.Ordinal)
                && reason.Contains("TopologyImportBlocked", StringComparison.Ordinal));
        var placementOmission = wall.GetProperty("placementOmission");
        Assert.Equal("topology_import_blocked", placementOmission.GetProperty("code").GetString());
        Assert.Equal("TopologyRepairBlocked", placementOmission.GetProperty("category").GetString());
        Assert.Contains(
            candidate.GetProperty("id").GetString(),
            placementOmission.GetProperty("repairCandidateIds").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            "detail-tooth-4",
            placementOmission.GetProperty("linkedWallIds").EnumerateArray().Select(item => item.GetString()));
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
        Assert.Equal("full", page.SvgProfile);
        Assert.True(page.DrawableItemCount > 0);
        Assert.True(page.VisibleDrawableItemCount > 0);
        Assert.True(page.HiddenDrawableItemCount >= 0);
        Assert.Contains("walls", page.VisibleLayerNames);
        Assert.True(page.PrimitiveCount >= 5);
        Assert.InRange(page.DetectionCoverage, 0, 1);
        Assert.True(page.ReviewQueueCount >= 0);
        Assert.NotNull(page.ReviewQueue);
        Assert.NotNull(page.ReviewQueueKindBreakdown);
        Assert.NotNull(page.ReviewQueueSeverityBreakdown);

        var walls = page.Layers.Single(layer => layer.Name == "walls");
        Assert.True(walls.Count >= 4);
        Assert.False(walls.Bounds.IsEmpty);
        Assert.False(walls.NormalizedBounds.IsEmpty);
        Assert.InRange(walls.NormalizedBounds.Left, 0, 1);
        Assert.InRange(walls.NormalizedBounds.Right, 0, 1);
        Assert.True(walls.NormalizedDensity > 0);
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
        Assert.Equal(
            "full",
            document.RootElement.GetProperty("pages")[0].GetProperty("svgProfile").GetString());
        Assert.Contains(
            document.RootElement.GetProperty("pages")[0].GetProperty("layers").EnumerateArray(),
            layer => layer.GetProperty("name").GetString() == "walls"
                && layer.GetProperty("count").GetInt32() >= 4);
        var jsonWallLayer = document.RootElement
            .GetProperty("pages")[0]
            .GetProperty("layers")
            .EnumerateArray()
            .Single(layer => layer.GetProperty("name").GetString() == "walls");
        Assert.Equal(JsonValueKind.Object, jsonWallLayer.GetProperty("normalizedBounds").ValueKind);
        Assert.True(jsonWallLayer.GetProperty("normalizedDensity").GetDouble() > 0);
        Assert.Equal(
            snapshot.ReviewQueueCount,
            document.RootElement.GetProperty("reviewQueueCount").GetInt32());
    }

    [Fact]
    public async Task VisualSnapshotExporter_RecordsStructuralReviewOverlayVisibility()
    {
        var result = await CreateScanResultAsync();
        var svgPaths = new Dictionary<int, string>
        {
            [1] = "overlays/page-1.svg"
        };

        var snapshot = PlanOverlaySnapshot.From(
            result,
            svgPaths,
            SvgOverlayRenderOptions.ForProfile(SvgOverlayRenderProfile.StructuralReview));

        var page = Assert.Single(snapshot.Pages);

        Assert.Equal("structural-review", page.SvgProfile);
        Assert.Contains("walls", page.VisibleLayerNames);
        Assert.Contains("rooms", page.VisibleLayerNames);
        Assert.Contains("openings", page.VisibleLayerNames);
        Assert.Contains("objects", page.HiddenLayerNames);
        Assert.Contains("objectAggregates", page.HiddenLayerNames);
        Assert.Contains("wallNodes", page.HiddenLayerNames);
        Assert.Contains("wallComponents", page.HiddenLayerNames);
        Assert.Contains("routingBarriers", page.HiddenLayerNames);
        Assert.True(page.VisibleDrawableItemCount > 0);
        Assert.True(page.HiddenDrawableItemCount > 0);
        Assert.Equal(
            page.DrawableItemCount,
            page.VisibleDrawableItemCount + page.HiddenDrawableItemCount);

        var json = PlanOverlaySnapshotJsonExporter.Serialize(
            snapshot,
            new PlanOverlaySnapshotJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(json);
        var jsonPage = document.RootElement.GetProperty("pages")[0];

        Assert.Equal("structural-review", jsonPage.GetProperty("svgProfile").GetString());
        Assert.Contains(
            jsonPage.GetProperty("hiddenLayerNames").EnumerateArray(),
            item => item.GetString() == "objects");
        Assert.Equal(
            page.VisibleDrawableItemCount,
            jsonPage.GetProperty("visibleDrawableItemCount").GetInt32());
    }

    [Fact]
    public async Task VisualSnapshotExporter_FlagsHighDensityLayerForVisualReview()
    {
        var result = await CreateScanResultAsync();
        var denseObjects = Enumerable.Range(0, 24)
            .Select(index => new ObjectCandidate(
                $"dense-symbol-{index}",
                1,
                ObjectCandidateKind.Symbol,
                new PlanRect(220 + (index % 6), 120 + (index / 6), 1, 1),
                Confidence.Medium)
            {
                Category = ObjectCategory.Unknown,
                SourceKind = ObjectCandidateSourceKind.CompositeLinework,
                Evidence = new[] { "synthetic dense visual QA cluster" }
            })
            .ToArray();

        result = result with
        {
            ObjectCandidates = result.ObjectCandidates.Concat(denseObjects).ToArray()
        };

        var snapshot = PlanOverlaySnapshot.From(result);
        var page = Assert.Single(snapshot.Pages);
        var objectLayer = page.Layers.Single(layer => layer.Name == "objects");

        Assert.False(objectLayer.NormalizedBounds.IsEmpty);
        Assert.True(objectLayer.NormalizedDensity >= 1000);
        Assert.Contains(page.Issues, issue =>
            issue.Code == "visual.layer_density_high"
            && issue.Severity == "warning"
            && issue.PageNumber == 1
            && issue.Message.Contains("objects", StringComparison.Ordinal));
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
        Assert.False(string.IsNullOrWhiteSpace(wall.GetProperty("properties").GetProperty("wallType").GetString()));
        Assert.False(wall.GetProperty("properties").GetProperty("excludedFromStructuralTopology").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(wall.GetProperty("properties").GetProperty("wallEvidenceCategory").GetString()));
        Assert.InRange(wall.GetProperty("properties").GetProperty("wallEvidenceConfidence").GetDouble(), 0, 1);
        Assert.True(wall.GetProperty("properties").GetProperty("wallEvidencePlacementReady").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(wall.GetProperty("properties").GetProperty("wallEvidenceRequiresReview").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(wall.GetProperty("properties").GetProperty("wallEvidenceRejectedAsNoise").ValueKind is JsonValueKind.True or JsonValueKind.False);
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
        var exportedWalls = root.GetProperty("walls").EnumerateArray().ToArray();
        Assert.Equal(exportedWalls.Length, summary.GetProperty("wallCount").GetInt32());
        Assert.Equal(
            exportedWalls.Count(item =>
                item.GetProperty("placementOmission").ValueKind == JsonValueKind.Null
                && item.GetProperty("reliability").GetProperty("readyForCoordinatePlacement").GetBoolean()),
            summary.GetProperty("placementReadyWallCount").GetInt32());
        Assert.Equal(
            exportedWalls.Count(item => item.GetProperty("placementOmission").ValueKind != JsonValueKind.Null),
            summary.GetProperty("placementOmittedWallCount").GetInt32());
        Assert.Equal(exportedWalls.Sum(item => item.GetProperty("topologySpans").GetArrayLength()), summary.GetProperty("wallTopologySpanCount").GetInt32());
        Assert.Equal(exportedWalls.Sum(item => item.GetProperty("solidSpans").GetArrayLength()), summary.GetProperty("wallSolidSpanCount").GetInt32());
        Assert.Empty(summary.GetProperty("wallPlacementOmissionCounts").EnumerateObject());
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
        var pageSummary = summary.GetProperty("pageSummaries")[0];
        Assert.Equal(summary.GetProperty("placementReadyWallCount").GetInt32(), pageSummary.GetProperty("placementReadyWallCount").GetInt32());
        Assert.Equal(summary.GetProperty("placementOmittedWallCount").GetInt32(), pageSummary.GetProperty("placementOmittedWallCount").GetInt32());
        Assert.Equal(summary.GetProperty("wallTopologySpanCount").GetInt32(), pageSummary.GetProperty("wallTopologySpanCount").GetInt32());
        Assert.Equal(summary.GetProperty("wallSolidSpanCount").GetInt32(), pageSummary.GetProperty("wallSolidSpanCount").GetInt32());
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
        Assert.False(string.IsNullOrWhiteSpace(wall.GetProperty("wallType").GetString()));
        Assert.True(wall.GetProperty("centerLine").GetProperty("start").GetProperty("x").GetDouble() > 0);
        Assert.Equal(JsonValueKind.Array, wall.GetProperty("wallGraphRepairCandidateIds").ValueKind);
        var evidenceAssessment = wall.GetProperty("evidenceAssessment");
        Assert.False(string.IsNullOrWhiteSpace(evidenceAssessment.GetProperty("category").GetString()));
        Assert.True(evidenceAssessment.GetProperty("placementReady").GetBoolean());
        Assert.False(evidenceAssessment.GetProperty("rejectedAsNoise").GetBoolean());
        Assert.True(evidenceAssessment.GetProperty("evidence").GetArrayLength() > 0);
        var topologySpan = Assert.Single(wall.GetProperty("topologySpans").EnumerateArray());
        Assert.Equal(wall.GetProperty("id").GetString(), topologySpan.GetProperty("wallId").GetString());
        Assert.True(topologySpan.GetProperty("drawingLength").GetDouble() > 0);
        Assert.Equal(JsonValueKind.Object, topologySpan.GetProperty("centerLine").ValueKind);
        var solidSpan = Assert.Single(wall.GetProperty("solidSpans").EnumerateArray());
        var bodyPolygon = solidSpan.GetProperty("bodyPolygon");
        Assert.Equal(5, bodyPolygon.GetArrayLength());
        Assert.Equal(bodyPolygon[0].GetRawText(), bodyPolygon[4].GetRawText());
        Assert.True(solidSpan.GetProperty("bodyBounds").GetProperty("width").GetDouble() > 0);
        Assert.True(solidSpan.GetProperty("bodyBounds").GetProperty("height").GetDouble() > 0);
        Assert.Equal(JsonValueKind.Object, solidSpan.GetProperty("alongVector").ValueKind);
        Assert.Equal(JsonValueKind.Object, solidSpan.GetProperty("normalVector").ValueKind);
        Assert.True(solidSpan.GetProperty("thicknessDrawingUnits").GetDouble() > 0);
        Assert.True(wall.GetProperty("reliability").GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.True(wall.GetProperty("reliability").GetProperty("reasons").ValueKind == JsonValueKind.Array);
        Assert.Equal(JsonValueKind.Null, wall.GetProperty("placementOmission").ValueKind);

        var wallGraph = root.GetProperty("wallGraph");
        Assert.Equal(
            wallGraph.GetProperty("nodes").GetArrayLength(),
            wallGraph.GetProperty("summary").GetProperty("nodeCount").GetInt32());
        Assert.Equal(
            wallGraph.GetProperty("edges").GetArrayLength(),
            wallGraph.GetProperty("summary").GetProperty("edgeCount").GetInt32());
        Assert.Equal(
            wallGraph.GetProperty("components").GetArrayLength(),
            wallGraph.GetProperty("summary").GetProperty("componentCount").GetInt32());
        Assert.Equal(
            root.GetProperty("wallGraphRepairCandidates").GetArrayLength(),
            wallGraph.GetProperty("summary").GetProperty("repairCandidateCount").GetInt32());
        var graphNode = Assert.Single(wallGraph.GetProperty("nodes").EnumerateArray(), node =>
            node.GetProperty("id").GetString() == topologySpan.GetProperty("fromNodeId").GetString());
        Assert.Equal(JsonValueKind.Object, graphNode.GetProperty("position").ValueKind);
        Assert.True(graphNode.GetProperty("degree").GetInt32() > 0);
        var graphEdge = Assert.Single(wallGraph.GetProperty("edges").EnumerateArray(), edge =>
            edge.GetProperty("id").GetString() == topologySpan.GetProperty("wallGraphEdgeId").GetString());
        Assert.Equal(topologySpan.GetProperty("fromNodeId").GetString(), graphEdge.GetProperty("fromNodeId").GetString());
        Assert.Equal(topologySpan.GetProperty("toNodeId").GetString(), graphEdge.GetProperty("toNodeId").GetString());
        Assert.Equal(topologySpan.GetProperty("wallId").GetString(), graphEdge.GetProperty("wallId").GetString());
        Assert.Equal("MainStructural", graphEdge.GetProperty("wallComponentKind").GetString());
        Assert.Equal(JsonValueKind.Object, graphEdge.GetProperty("centerLine").ValueKind);
        Assert.True(graphEdge.GetProperty("drawingLength").GetDouble() > 0);
        Assert.Contains(wallGraph.GetProperty("components").EnumerateArray(), component =>
            component.GetProperty("kind").GetString() == "MainStructural"
            && component.GetProperty("edgeCount").GetInt32() > 0);

        var room = root.GetProperty("rooms")[0];
        Assert.Equal("ROOM", room.GetProperty("label").GetString());
        Assert.True(room.GetProperty("boundary").GetArrayLength() >= 4);

        Assert.True(root.GetProperty("routingLayer").TryGetProperty("barriers", out _));
        Assert.True(root.GetProperty("issues").ValueKind == JsonValueKind.Array);
    }

    [Fact]
    public async Task PlacementExporter_UsesPairedFaceEvidenceForSolidSpanBodyPolygon()
    {
        var result = await CreateScanResultAsync();
        var sourceWall = result.Walls.First(wall => wall.CenterLine.IsHorizontal());
        var line = sourceWall.CenterLine;
        var firstFace = new PlanLineSegment(
            new PlanPoint(line.Start.X, line.Start.Y - 3),
            new PlanPoint(line.End.X, line.End.Y - 3));
        var secondFace = new PlanLineSegment(
            new PlanPoint(line.Start.X, line.Start.Y + 7),
            new PlanPoint(line.End.X, line.End.Y + 7));
        var wallWithPairEvidence = sourceWall with
        {
            Thickness = 10,
            PairEvidence = new WallPairEvidence(
                firstFace,
                secondFace,
                10,
                1,
                0.95,
                1,
                1,
                ["first-face"],
                ["second-face"])
        };
        result = result with
        {
            Walls = result.Walls
                .Select(wall => wall.Id == sourceWall.Id ? wallWithPairEvidence : wall)
                .ToArray()
        };

        using var document = JsonDocument.Parse(PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false }));
        var wall = document.RootElement
            .GetProperty("walls")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == sourceWall.Id);
        var span = Assert.Single(wall.GetProperty("solidSpans").EnumerateArray());
        var body = span.GetProperty("bodyPolygon");
        var evidence = span.GetProperty("evidence").EnumerateArray().Select(item => item.GetString()).ToArray();

        Assert.Equal(5, body.GetArrayLength());
        Assert.Equal(line.Start.X, body[0].GetProperty("x").GetDouble(), 3);
        Assert.Equal(line.Start.Y - 3, body[0].GetProperty("y").GetDouble(), 3);
        Assert.Equal(line.End.X, body[1].GetProperty("x").GetDouble(), 3);
        Assert.Equal(line.End.Y - 3, body[1].GetProperty("y").GetDouble(), 3);
        Assert.Equal(line.End.X, body[2].GetProperty("x").GetDouble(), 3);
        Assert.Equal(line.End.Y + 7, body[2].GetProperty("y").GetDouble(), 3);
        Assert.Equal(line.Start.X, body[3].GetProperty("x").GetDouble(), 3);
        Assert.Equal(line.Start.Y + 7, body[3].GetProperty("y").GetDouble(), 3);
        Assert.Equal(10, span.GetProperty("bodyBounds").GetProperty("height").GetDouble(), 3);
        Assert.Equal(1, span.GetProperty("normalVector").GetProperty("y").GetDouble(), 3);
        Assert.Contains(evidence, item => item?.Contains("detected paired wall-face evidence", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task PlacementExporter_UsesWallEvidenceAssessmentForReviewReadiness()
    {
        var result = await CreateScanResultAsync();
        var firstWall = result.Walls[0];
        result = result with
        {
            WallEvidenceMap = new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                new[]
                {
                    new WallEvidenceWallAssessment(
                        firstWall.Id,
                        firstWall.PageNumber,
                        firstWall.Bounds,
                        WallEvidenceCategory.WeakSingleLine,
                        new Confidence(0.44),
                        PlacementReady: false,
                        RequiresReview: true,
                        RejectedAsNoise: false,
                        SourcePrimitiveIds: firstWall.SourcePrimitiveIds,
                        Evidence: new[] { "test evidence: weak wall candidate should not be coordinate-ready" })
                })
        };

        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(placementJson);
        var wall = document.RootElement
            .GetProperty("walls")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == firstWall.Id);

        var evidenceAssessment = wall.GetProperty("evidenceAssessment");
        Assert.Equal("WeakSingleLine", evidenceAssessment.GetProperty("category").GetString());
        Assert.False(evidenceAssessment.GetProperty("placementReady").GetBoolean());
        Assert.True(evidenceAssessment.GetProperty("requiresReview").GetBoolean());

        var reliability = wall.GetProperty("reliability");
        Assert.False(reliability.GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.True(reliability.GetProperty("requiresReview").GetBoolean());
        Assert.Contains(
            reliability.GetProperty("reasons").EnumerateArray(),
            reason => reason.GetString()?.Contains("wall evidence not placement-ready", StringComparison.OrdinalIgnoreCase) == true);

        var placementOmission = wall.GetProperty("placementOmission");
        Assert.Equal("wall_evidence_review_required", placementOmission.GetProperty("code").GetString());
        Assert.Equal("WallEvidenceReview", placementOmission.GetProperty("category").GetString());
        Assert.Contains(
            placementOmission.GetProperty("evidence").EnumerateArray(),
            evidence => evidence.GetString()?.Contains("weak wall candidate", StringComparison.OrdinalIgnoreCase) == true);
        Assert.False(string.IsNullOrWhiteSpace(placementOmission.GetProperty("recommendedAction").GetString()));

        var summary = document.RootElement.GetProperty("summary");
        var exportedWalls = document.RootElement.GetProperty("walls").EnumerateArray().ToArray();
        Assert.Equal(
            exportedWalls.Count(item =>
                item.GetProperty("placementOmission").ValueKind == JsonValueKind.Null
                && item.GetProperty("reliability").GetProperty("readyForCoordinatePlacement").GetBoolean()),
            summary.GetProperty("placementReadyWallCount").GetInt32());
        Assert.Equal(
            exportedWalls.Count(item => item.GetProperty("placementOmission").ValueKind != JsonValueKind.Null),
            summary.GetProperty("placementOmittedWallCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("wallPlacementOmissionCounts").GetProperty("wall_evidence_review_required").GetInt32());

        var issue = Assert.Single(document.RootElement.GetProperty("issues").EnumerateArray(), item =>
            item.GetProperty("code").GetString() == "placement.review.wall_evidence_requires_review");
        Assert.Equal(firstWall.Id, issue.GetProperty("itemId").GetString());
        Assert.Equal("Warning", issue.GetProperty("severity").GetString());
        Assert.Equal("WeakSingleLine", issue.GetProperty("properties").GetProperty("category").GetString());
        Assert.Equal("Review", issue.GetProperty("properties").GetProperty("decision").GetString());
        Assert.Contains(
            issue.GetProperty("evidence").EnumerateArray(),
            evidence => evidence.GetString()?.Contains("weak wall candidate", StringComparison.OrdinalIgnoreCase) == true);

        var importReadiness = document.RootElement.GetProperty("summary").GetProperty("importReadiness");
        Assert.Contains(
            "placement.wall_evidence.requires_review",
            JsonStrings(importReadiness.GetProperty("reviewIssueCodes")));
        Assert.DoesNotContain(
            "placement.review.wall_evidence_requires_review",
            JsonStrings(importReadiness.GetProperty("reviewIssueCodes")));
    }

    [Fact]
    public async Task PlacementExporter_MarksRoomsForReviewWhenBoundaryWallEvidenceRequiresReview()
    {
        var result = await CreateScanResultAsync();
        var roomRegion = result.Rooms.First(room => room.WallIds.Count > 0);
        var boundaryWallId = roomRegion.WallIds[0];
        var boundaryWall = result.Walls.Single(wall => wall.Id == boundaryWallId);
        result = result with
        {
            WallEvidenceMap = new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                new[]
                {
                    new WallEvidenceWallAssessment(
                        boundaryWall.Id,
                        boundaryWall.PageNumber,
                        boundaryWall.Bounds,
                        WallEvidenceCategory.WeakSingleLine,
                        new Confidence(0.48),
                        PlacementReady: false,
                        RequiresReview: true,
                        RejectedAsNoise: false,
                        SourcePrimitiveIds: boundaryWall.SourcePrimitiveIds,
                        Evidence: new[] { "test evidence: boundary wall requires review" })
                    {
                        Decision = WallEvidenceDecision.Review
                    }
                })
        };

        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(placementJson);
        var room = document.RootElement
            .GetProperty("rooms")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == roomRegion.Id);

        var reliability = room.GetProperty("reliability");
        Assert.False(reliability.GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.True(reliability.GetProperty("requiresReview").GetBoolean());
        Assert.Contains(
            reliability.GetProperty("reasons").EnumerateArray(),
            reason => reason.GetString()?.Contains("room boundary uses review-required wall evidence", StringComparison.OrdinalIgnoreCase) == true
                && reason.GetString()?.Contains(boundaryWallId, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task PlacementExporter_MarksOpeningsForReviewWhenHostWallEvidenceRequiresReview()
    {
        var result = await CreateScanResultAsync();
        var hostWall = result.Walls[0];
        var hostWallId = hostWall.Id;
        var openingCandidate = AnchoredOpening("opening-review-host-wall", hostWall);
        result = result with
        {
            Openings = result.Openings
                .Concat(new[] { openingCandidate })
                .ToArray(),
            WallEvidenceMap = new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                new[]
                {
                    new WallEvidenceWallAssessment(
                        hostWall.Id,
                        hostWall.PageNumber,
                        hostWall.Bounds,
                        WallEvidenceCategory.WeakSingleLine,
                        new Confidence(0.47),
                        PlacementReady: false,
                        RequiresReview: true,
                        RejectedAsNoise: false,
                        SourcePrimitiveIds: hostWall.SourcePrimitiveIds,
                        Evidence: new[] { "test evidence: opening host wall requires review" })
                    {
                        Decision = WallEvidenceDecision.Review
                    }
                })
        };

        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(placementJson);
        var opening = document.RootElement
            .GetProperty("openings")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == openingCandidate.Id);

        var reliability = opening.GetProperty("reliability");
        Assert.False(reliability.GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.True(reliability.GetProperty("requiresReview").GetBoolean());
        Assert.Contains(
            reliability.GetProperty("reasons").EnumerateArray(),
            reason => reason.GetString()?.Contains("opening placement uses review-required wall evidence", StringComparison.OrdinalIgnoreCase) == true
                && reason.GetString()?.Contains(hostWallId, StringComparison.Ordinal) == true);

        static OpeningCandidate AnchoredOpening(string id, WallSegment hostWall)
        {
            const double startParameter = 0.35;
            const double endParameter = 0.45;
            const double centerParameter = (startParameter + endParameter) / 2.0;
            var referenceLine = hostWall.CenterLine;
            var startPoint = referenceLine.PointAt(startParameter);
            var endPoint = referenceLine.PointAt(endParameter);
            var openingLine = new PlanLineSegment(startPoint, endPoint);
            var length = referenceLine.Length;
            var openingLength = openingLine.Length;
            var alongVector = new PlanVector(
                referenceLine.End.X - referenceLine.Start.X,
                referenceLine.End.Y - referenceLine.Start.Y).Normalize();
            var normalVector = new PlanVector(-alongVector.Y, alongVector.X);
            var depth = Math.Max(hostWall.Thickness, 4);
            var footprint = openingLine.Bounds.Inflate(depth / 2.0);

            return new OpeningCandidate(
                id,
                hostWall.PageNumber,
                OpeningType.Door,
                footprint,
                Confidence.High)
            {
                HostWallIds = [hostWall.Id],
                CenterLine = openingLine,
                Orientation = openingLine.IsHorizontal()
                    ? OpeningOrientation.Horizontal
                    : openingLine.IsVertical()
                        ? OpeningOrientation.Vertical
                        : OpeningOrientation.Unknown,
                Operation = OpeningOperation.Hinged,
                Placement = new OpeningPlacement(
                    hostWall.Id,
                    [hostWall.Id],
                    referenceLine,
                    startPoint,
                    endPoint,
                    startParameter * length,
                    endParameter * length,
                    centerParameter * length,
                    openingLength,
                    footprint,
                    [
                        new PlanPoint(footprint.Left, footprint.Top),
                        new PlanPoint(footprint.Right, footprint.Top),
                        new PlanPoint(footprint.Right, footprint.Bottom),
                        new PlanPoint(footprint.Left, footprint.Bottom)
                    ],
                    new PlanLineSegment(startPoint, startPoint.Translate(normalVector.X * depth, normalVector.Y * depth)),
                    new PlanLineSegment(endPoint, endPoint.Translate(normalVector.X * depth, normalVector.Y * depth)),
                    depth,
                    null,
                    null,
                    null,
                    null,
                    null,
                    startParameter,
                    endParameter,
                    centerParameter,
                    alongVector,
                    normalVector,
                    0,
                    Confidence.High,
                    ["synthetic opening placement for wall evidence reliability test"]),
                SourcePrimitiveIds = ["opening-review-host-wall-source"],
                Evidence = ["synthetic anchored opening"]
            };
        }
    }

    [Fact]
    public void OpeningPlacementExport_NormalizesReversedHostWallOffsets()
    {
        var referenceLine = new PlanLineSegment(new PlanPoint(100, 40), new PlanPoint(0, 40));
        var startPoint = new PlanPoint(25, 40);
        var endPoint = new PlanPoint(35, 40);
        var footprint = new PlanRect(25, 38, 10, 4);
        var placement = new OpeningPlacement(
            "wall-reversed",
            ["wall-reversed"],
            referenceLine,
            startPoint,
            endPoint,
            75,
            65,
            70,
            10,
            footprint,
            [
                new PlanPoint(25, 38),
                new PlanPoint(35, 38),
                new PlanPoint(35, 42),
                new PlanPoint(25, 42)
            ],
            new PlanLineSegment(new PlanPoint(25, 38), new PlanPoint(25, 42)),
            new PlanLineSegment(new PlanPoint(35, 38), new PlanPoint(35, 42)),
            4,
            null,
            null,
            null,
            null,
            null,
            0.75,
            0.65,
            0.7,
            new PlanVector(-1, 0),
            new PlanVector(0, -1),
            0,
            Confidence.High,
            ["synthetic reversed placement"]);

        var exported = OpeningPlacementExport.From(placement);

        Assert.Equal(65, exported.StartOffsetDrawingUnits, 3);
        Assert.Equal(75, exported.EndOffsetDrawingUnits, 3);
        Assert.Equal(10, exported.LengthDrawingUnits, 3);
        Assert.Equal(0.65, exported.HostWallStartParameter, 3);
        Assert.Equal(0.75, exported.HostWallEndParameter, 3);
        Assert.Equal(35, exported.StartPoint.X, 3);
        Assert.Equal(25, exported.EndPoint.X, 3);
        Assert.Equal(exported.FootprintCorners[0], exported.StartJambLine.Start);
        Assert.Equal(exported.FootprintCorners[3], exported.StartJambLine.End);
        Assert.Equal(exported.FootprintCorners[1], exported.EndJambLine.Start);
        Assert.Equal(exported.FootprintCorners[2], exported.EndJambLine.End);
        Assert.Contains(
            exported.Evidence,
            item => item.Contains("normalized reversed opening placement offset order", StringComparison.OrdinalIgnoreCase));
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
                        },
                        new PlanDiagnostic(
                            "wall_graph.endpoint_overrun.review",
                            DiagnosticSeverity.Warning,
                            "wall-graph",
                            "A wall endpoint extends beyond a supported junction but was too long to trim automatically.")
                        {
                            Scope = DiagnosticScope.Detection,
                            PageNumber = 1,
                            Region = new PlanRect(88, 92, 28, 24),
                            Confidence = Confidence.Medium,
                            SourcePrimitiveIds = new[] { "wall-top", "wall-left" },
                            Properties = new Dictionary<string, string>
                            {
                                ["overrunKind"] = "EndpointOverrun",
                                ["overrunDistance"] = "80",
                                ["nodeId"] = "page:1:node:6",
                                ["targetNodeId"] = "page:1:node:1",
                                ["wallId"] = "page:1:wall:1",
                                ["wallIds"] = "page:1:wall:1,page:1:wall:4"
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

        var wallOverrun = Assert.Single(issues, issue =>
            issue.GetProperty("code").GetString() == "placement.review.wall_graph_endpoint_overrun");
        Assert.Equal(88, wallOverrun.GetProperty("bounds").GetProperty("x").GetDouble());
        Assert.Equal(880, wallOverrun.GetProperty("boundsMillimeters").GetProperty("x").GetDouble(), precision: 3);
        Assert.Equal("EndpointOverrun", wallOverrun.GetProperty("properties").GetProperty("overrunKind").GetString());
        Assert.Contains("wall-top", JsonStrings(wallOverrun.GetProperty("sourcePrimitiveIds")));
        Assert.Contains("endpoint-overrun trim", wallOverrun.GetProperty("recommendedAction").GetString());

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
    public void PlacementExporter_WritesDenseMinorRoutingDetailIssues()
    {
        var result = WithReliableCalibrationAndMeasurement(
            CreateDenseMinorRoutingDetailResult(),
            CreateMeasurementReport(consistentCount: 1, outlierCount: 0, spreadRatio: 1.0));

        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(placementJson);
        var issues = document.RootElement.GetProperty("issues").EnumerateArray().ToArray();
        var issue = Assert.Single(issues, item =>
            item.GetProperty("code").GetString() == "placement.review.dense_minor_routing_detail");

        Assert.Equal("Info", issue.GetProperty("severity").GetString());
        Assert.Equal(1, issue.GetProperty("pageNumber").GetInt32());
        Assert.Equal("routing-dense-minor-detail:p1:1", issue.GetProperty("itemId").GetString());
        Assert.Equal(98, issue.GetProperty("bounds").GetProperty("x").GetDouble());
        Assert.Equal(980, issue.GetProperty("boundsMillimeters").GetProperty("x").GetDouble(), precision: 3);
        Assert.Contains("detail-host", JsonStrings(issue.GetProperty("sourcePrimitiveIds")));
        Assert.Contains("detail-tooth-4", JsonStrings(issue.GetProperty("sourcePrimitiveIds")));
        Assert.Contains("Wall", JsonStrings(issue.GetProperty("sourceLayers")));
        Assert.Equal("detail-host", issue.GetProperty("properties").GetProperty("hostWallId").GetString());
        Assert.Equal("4", issue.GetProperty("properties").GetProperty("minorDetailWallCount").GetString());
        Assert.Contains("detail-tooth-1", issue.GetProperty("properties").GetProperty("incidentWallIds").GetString());
        Assert.Contains("non-structural routing detail", issue.GetProperty("recommendedAction").GetString());
        Assert.Contains(
            issue.GetProperty("evidence").EnumerateArray(),
            item => item.GetString()?.Contains("suppressed from trusted routing barriers", StringComparison.Ordinal) == true);

        var routingBarriers = document.RootElement
            .GetProperty("routingLayer")
            .GetProperty("barriers")
            .EnumerateArray()
            .ToArray();
        Assert.DoesNotContain(routingBarriers, barrier =>
            barrier.GetProperty("sourceId").GetString()?.StartsWith("detail-", StringComparison.Ordinal) == true);
        Assert.Contains(
            document.RootElement.GetProperty("summary").GetProperty("evidence").EnumerateArray(),
            item => item.GetString()?.Contains("placement issue", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(
            document.RootElement.GetProperty("summary").GetProperty("importReadiness").GetProperty("reviewIssueCodes").EnumerateArray(),
            item => item.GetString() == "placement.review.dense_minor_routing_detail");

        var hostWall = document.RootElement
            .GetProperty("walls")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "detail-host");
        var hostTopologySpans = hostWall.GetProperty("topologySpans").EnumerateArray().ToArray();
        Assert.Equal(5, hostTopologySpans.Length);
        Assert.Equal("edge-host-1", hostTopologySpans[0].GetProperty("wallGraphEdgeId").GetString());
        Assert.Equal(100, hostTopologySpans[0].GetProperty("centerLine").GetProperty("start").GetProperty("x").GetDouble());
        Assert.Equal(140, hostTopologySpans[0].GetProperty("centerLine").GetProperty("end").GetProperty("x").GetDouble());
        Assert.Equal(1000, hostTopologySpans[0].GetProperty("centerLineMillimeters").GetProperty("start").GetProperty("x").GetDouble());
        Assert.Equal(0, hostTopologySpans[0].GetProperty("sourceWallStartOffsetDrawingUnits").GetDouble(), precision: 3);
        Assert.Equal(40, hostTopologySpans[0].GetProperty("sourceWallEndOffsetDrawingUnits").GetDouble(), precision: 3);
        Assert.Equal(400, hostTopologySpans[0].GetProperty("sourceWallEndOffsetMillimeters").GetDouble(), precision: 3);
        Assert.Equal(0.2, hostTopologySpans[0].GetProperty("sourceWallEndParameter").GetDouble(), precision: 3);
        Assert.Equal(0, hostTopologySpans[0].GetProperty("sourceWallStartProjectionDistanceDrawingUnits").GetDouble(), precision: 3);
        Assert.Equal(0, hostTopologySpans[0].GetProperty("sourceWallEndProjectionDistanceDrawingUnits").GetDouble(), precision: 3);
    }

    [Fact]
    public async Task JsonExporter_WritesEvidenceBackedScanReviewQueue()
    {
        var result = await CreateScanResultAsync();
        var reviewWall = result.Walls[0];
        result = result with
        {
            WallEvidenceMap = new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                new[]
                {
                    new WallEvidenceWallAssessment(
                        "wall-evidence-review",
                        reviewWall.PageNumber,
                        new PlanRect(100, 100, 200, 14),
                        WallEvidenceCategory.WeakSingleLine,
                        new Confidence(0.42),
                        PlacementReady: false,
                        RequiresReview: true,
                        RejectedAsNoise: false,
                        SourcePrimitiveIds: new[] { "wall-top", "wall-right" },
                        Evidence: new[] { "synthetic weak wall candidate requires review" })
                    {
                        Decision = WallEvidenceDecision.Review,
                        ScoreBreakdown = new WallEvidenceScoreBreakdown(
                            PositiveScore: 0.25,
                            NegativeScore: 0.68,
                            DecisionScore: -0.43,
                            PairSupportScore: 0,
                            LayerSupportScore: 0.2,
                            StructuralSupportScore: 0.1,
                            RecoverySupportScore: 0,
                            NoisePenalty: 0.35,
                            FragmentReviewPenalty: 0.55,
                            PositiveEvidence: new[] { "nearby structural wall endpoint support" },
                            NegativeEvidence: new[] { "short weak single-line fragment" })
                    }
                }),
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
                        },
                        new PlanDiagnostic(
                            "wall_graph.endpoint_overrun.review",
                            DiagnosticSeverity.Warning,
                            "wall-graph",
                            "A wall endpoint extends beyond a supported junction but was too long to trim automatically.")
                        {
                            Scope = DiagnosticScope.Detection,
                            PageNumber = 1,
                            Region = new PlanRect(88, 92, 28, 24),
                            Confidence = Confidence.Medium,
                            SourcePrimitiveIds = new[] { "wall-top", "wall-left" },
                            Properties = new Dictionary<string, string>
                            {
                                ["overrunKind"] = "EndpointOverrun",
                                ["overrunDistance"] = "80",
                                ["nodeId"] = "page:1:node:6",
                                ["targetNodeId"] = "page:1:node:1",
                                ["wallId"] = "page:1:wall:1",
                                ["wallIds"] = "page:1:wall:1,page:1:wall:4"
                            }
                        }
                    })
                    .ToArray()
            }
        };

        var json = PlanTraceJsonExporter.Serialize(result);
        using var document = JsonDocument.Parse(json);
        var queue = document.RootElement.GetProperty("reviewQueue").EnumerateArray().ToArray();
        var importReadiness = document.RootElement.GetProperty("importReadiness");

        Assert.Contains(
            "placement.wall_evidence.requires_review",
            JsonStrings(importReadiness.GetProperty("reviewIssueCodes")));

        Assert.Contains(queue, item =>
            item.GetProperty("kind").GetString() == "MeasurementOutlier"
            && item.GetProperty("itemId").GetString() == "dimension-outlier"
            && item.GetProperty("bounds").ValueKind == JsonValueKind.Object
            && item.GetProperty("properties").GetProperty("metricImportImpact").GetString() == "ReviewOnly");
        Assert.Contains(queue, item =>
            item.GetProperty("kind").GetString() == "WallEvidenceReview"
            && item.GetProperty("itemId").GetString() == "wall-evidence-review"
            && item.GetProperty("severity").GetString() == "Warning"
            && item.GetProperty("bounds").GetProperty("x").GetDouble() == 100
            && item.GetProperty("properties").GetProperty("category").GetString() == "WeakSingleLine"
            && item.GetProperty("properties").GetProperty("decision").GetString() == "Review"
            && item.GetProperty("properties").GetProperty("placementReady").GetString() == "False"
            && item.GetProperty("properties").GetProperty("reviewQueueRank").GetString() == "1"
            && item.GetProperty("properties").GetProperty("reviewQueueReason").GetString()!.Contains("not placement-ready", StringComparison.Ordinal)
            && item.GetProperty("recommendedAction").GetString()?.Contains("coordinate-ready structural geometry", StringComparison.Ordinal) == true
            && item.GetProperty("evidence").EnumerateArray().Any(evidence =>
                evidence.GetString()?.Contains("short weak single-line fragment", StringComparison.Ordinal) == true));
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
        Assert.Contains(queue, item =>
            item.GetProperty("kind").GetString() == "WallGraphGapReview"
            && item.GetProperty("itemId").GetString() == "wall_graph.endpoint_overrun.review"
            && item.GetProperty("bounds").GetProperty("x").GetDouble() == 88
            && item.GetProperty("properties").GetProperty("overrunKind").GetString() == "EndpointOverrun"
            && item.GetProperty("properties").GetProperty("reviewQueueReason").GetString()!.Contains("overrun 80 drawing unit", StringComparison.Ordinal)
            && item.GetProperty("recommendedAction").GetString()?.Contains("endpoint-overrun trim", StringComparison.Ordinal) == true);
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

    private static PlanScanResult CreateDenseMinorRoutingDetailResult()
    {
        var host = SyntheticWall("detail-host", 100, 100, 300, 100);
        var teeth = new[]
        {
            SyntheticWall("detail-tooth-1", 140, 100, 140, 124),
            SyntheticWall("detail-tooth-2", 180, 100, 180, 124),
            SyntheticWall("detail-tooth-3", 220, 100, 220, 124),
            SyntheticWall("detail-tooth-4", 260, 100, 260, 124)
        };
        var walls = new[] { host }.Concat(teeth).ToArray();
        var component = new WallGraphComponent(
            "component-secondary",
            1,
            WallGraphComponentKind.SecondaryStructural,
            new PlanRect(100, 100, 200, 24),
            walls.Select(wall => wall.Id).ToArray(),
            ["node-left", "node-t1", "node-t2", "node-t3", "node-t4", "node-right", "node-t1-end", "node-t2-end", "node-t3-end", "node-t4-end"],
            ["edge-host-1", "edge-host-2", "edge-host-3", "edge-host-4", "edge-host-5", "edge-tooth-1", "edge-tooth-2", "edge-tooth-3", "edge-tooth-4"],
            walls.SelectMany(wall => wall.SourcePrimitiveIds).ToArray(),
            walls.Sum(wall => wall.DrawingLength),
            Confidence.Medium,
            ["synthetic secondary detail component"]);
        var now = DateTimeOffset.UtcNow;
        return new PlanScanResult(
            new PlanDocument(
                "dense-minor-routing-detail",
                [
                    new PlanPage(
                        1,
                        new PlanSize(500, 400),
                        walls.Select(wall => WallLine(
                                wall.Id,
                                wall.CenterLine.Start,
                                wall.CenterLine.End))
                            .Cast<PlanPrimitive>()
                            .ToArray())
                ])
            {
                Metadata = new PlanMetadata
                {
                    SourceName = "dense-minor-routing-detail.pdf",
                    SourcePath = @"C:\plans\dense-minor-routing-detail.pdf",
                    Properties = new Dictionary<string, string>
                    {
                        ["format"] = "pdf",
                        ["loader"] = "synthetic",
                        ["sourceKind"] = PlanSourceKind.Pdf.ToString(),
                        ["effectiveSourceKind"] = PlanSourceKind.Pdf.ToString()
                    }
                }
            },
            PlanLayerAnalysis.Empty,
            PlanCalibration.Empty,
            MeasurementConsistencyReport.Empty,
            Array.Empty<TitleBlockAnalysis>(),
            Array.Empty<DimensionAnnotation>(),
            Array.Empty<PlanAnnotationBlock>(),
            Array.Empty<GridAxis>(),
            Array.Empty<GridBaySpacing>(),
            Array.Empty<SheetRegion>(),
            Array.Empty<SurfacePatternCandidate>(),
            walls,
            new WallGraph(
                [
                    SyntheticNode("node-left", 100, 100, WallNodeKind.Endpoint),
                    SyntheticNode("node-t1", 140, 100, WallNodeKind.TJunction),
                    SyntheticNode("node-t2", 180, 100, WallNodeKind.TJunction),
                    SyntheticNode("node-t3", 220, 100, WallNodeKind.TJunction),
                    SyntheticNode("node-t4", 260, 100, WallNodeKind.TJunction),
                    SyntheticNode("node-right", 300, 100, WallNodeKind.Endpoint),
                    SyntheticNode("node-t1-end", 140, 124, WallNodeKind.Endpoint),
                    SyntheticNode("node-t2-end", 180, 124, WallNodeKind.Endpoint),
                    SyntheticNode("node-t3-end", 220, 124, WallNodeKind.Endpoint),
                    SyntheticNode("node-t4-end", 260, 124, WallNodeKind.Endpoint)
                ],
                [
                    new WallEdge("edge-host-1", 1, "node-left", "node-t1", host.Id, Confidence.High),
                    new WallEdge("edge-host-2", 1, "node-t1", "node-t2", host.Id, Confidence.High),
                    new WallEdge("edge-host-3", 1, "node-t2", "node-t3", host.Id, Confidence.High),
                    new WallEdge("edge-host-4", 1, "node-t3", "node-t4", host.Id, Confidence.High),
                    new WallEdge("edge-host-5", 1, "node-t4", "node-right", host.Id, Confidence.High),
                    new WallEdge("edge-tooth-1", 1, "node-t1", "node-t1-end", teeth[0].Id, Confidence.High),
                    new WallEdge("edge-tooth-2", 1, "node-t2", "node-t2-end", teeth[1].Id, Confidence.High),
                    new WallEdge("edge-tooth-3", 1, "node-t3", "node-t3-end", teeth[2].Id, Confidence.High),
                    new WallEdge("edge-tooth-4", 1, "node-t4", "node-t4-end", teeth[3].Id, Confidence.High)
                ],
                [component]),
            Array.Empty<RoomRegion>(),
            RoomAdjacencyGraph.Empty,
            Array.Empty<OpeningCandidate>(),
            Array.Empty<ObjectCandidate>(),
            Array.Empty<ObjectCandidateGroup>(),
            Array.Empty<ObjectAggregate>(),
            new PipelineDiagnostics(
                now,
                now,
                Array.Empty<PipelineStageReport>(),
              Array.Empty<PlanDiagnostic>()));
      }

    private static PlanScanResult CreateJitteredPlacementRunResult()
    {
        var walls = new[]
        {
            SyntheticWall("jitter-wall-a", 100, 60, 100, 130) with { WallType = WallType.Interior },
            SyntheticWall("jitter-wall-b", 102, 130, 102, 205) with { WallType = WallType.Interior },
            SyntheticWall("jitter-wall-c", 98.8, 205, 98.8, 275) with { WallType = WallType.Interior }
        };
        var nodes = new[]
        {
            SyntheticNode("jitter-node-a1", 100, 60, WallNodeKind.Endpoint),
            SyntheticNode("jitter-node-a2", 100, 130, WallNodeKind.Endpoint),
            SyntheticNode("jitter-node-b1", 102, 130, WallNodeKind.Endpoint),
            SyntheticNode("jitter-node-b2", 102, 205, WallNodeKind.Endpoint),
            SyntheticNode("jitter-node-c1", 98.8, 205, WallNodeKind.Endpoint),
            SyntheticNode("jitter-node-c2", 98.8, 275, WallNodeKind.Endpoint)
        };
        var edges = new[]
        {
            new WallEdge("jitter-edge-a", 1, "jitter-node-a1", "jitter-node-a2", walls[0].Id, Confidence.High),
            new WallEdge("jitter-edge-b", 1, "jitter-node-b1", "jitter-node-b2", walls[1].Id, Confidence.High),
            new WallEdge("jitter-edge-c", 1, "jitter-node-c1", "jitter-node-c2", walls[2].Id, Confidence.High)
        };
        var component = new WallGraphComponent(
            "jitter-component",
            1,
            WallGraphComponentKind.SecondaryStructural,
            new PlanRect(96, 56, 10, 223),
            walls.Select(wall => wall.Id).ToArray(),
            nodes.Select(node => node.Id).ToArray(),
            edges.Select(edge => edge.Id).ToArray(),
            walls.SelectMany(wall => wall.SourcePrimitiveIds).ToArray(),
            walls.Sum(wall => wall.DrawingLength),
            Confidence.High,
            ["synthetic nearly-collinear placement run"]);
        var assessments = walls
            .Select(wall => new WallEvidenceWallAssessment(
                wall.Id,
                wall.PageNumber,
                wall.Bounds,
                WallEvidenceCategory.StrongWallBody,
                Confidence.High,
                PlacementReady: true,
                RequiresReview: false,
                RejectedAsNoise: false,
                wall.SourcePrimitiveIds,
                ["synthetic accepted wall body evidence"])
            {
                Decision = WallEvidenceDecision.Accept
            })
            .ToArray();
        var now = DateTimeOffset.UtcNow;

        return new PlanScanResult(
            new PlanDocument(
                "jittered-placement-run",
                [
                    new PlanPage(
                        1,
                        new PlanSize(320, 340),
                        walls.Select(wall => WallLine(
                                wall.Id,
                                wall.CenterLine.Start,
                                wall.CenterLine.End))
                            .Cast<PlanPrimitive>()
                            .ToArray())
                ])
            {
                Metadata = new PlanMetadata
                {
                    SourceName = "jittered-placement-run.pdf",
                    SourcePath = @"C:\plans\jittered-placement-run.pdf",
                    Properties = new Dictionary<string, string>
                    {
                        ["format"] = "pdf",
                        ["loader"] = "synthetic",
                        ["sourceKind"] = PlanSourceKind.Pdf.ToString(),
                        ["effectiveSourceKind"] = PlanSourceKind.Pdf.ToString()
                    }
                }
            },
            PlanLayerAnalysis.Empty,
            PlanCalibration.Empty,
            MeasurementConsistencyReport.Empty,
            Array.Empty<TitleBlockAnalysis>(),
            Array.Empty<DimensionAnnotation>(),
            Array.Empty<PlanAnnotationBlock>(),
            Array.Empty<GridAxis>(),
            Array.Empty<GridBaySpacing>(),
            Array.Empty<SheetRegion>(),
            Array.Empty<SurfacePatternCandidate>(),
            walls,
            new WallGraph(nodes, edges, [component]),
            Array.Empty<RoomRegion>(),
            RoomAdjacencyGraph.Empty,
            Array.Empty<OpeningCandidate>(),
            Array.Empty<ObjectCandidate>(),
            Array.Empty<ObjectCandidateGroup>(),
            Array.Empty<ObjectAggregate>(),
            new PipelineDiagnostics(
                now,
                now,
                Array.Empty<PipelineStageReport>(),
                Array.Empty<PlanDiagnostic>()))
        {
            WallEvidenceMap = new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                assessments,
                walls.Length,
                0)
        };
    }

    private static PlanScanResult CreateOpeningCutoutPlacementReviewResult()
    {
        var wall = SyntheticWall("cutout-wall", 80, 120, 280, 120) with { WallType = WallType.Interior };
        var nodes = new[]
        {
            SyntheticNode("cutout-node-a", 80, 120, WallNodeKind.Endpoint),
            SyntheticNode("cutout-node-b", 280, 120, WallNodeKind.Endpoint)
        };
        var edge = new WallEdge("cutout-edge", 1, nodes[0].Id, nodes[1].Id, wall.Id, Confidence.High);
        var component = new WallGraphComponent(
            "cutout-component",
            1,
            WallGraphComponentKind.MainStructural,
            wall.Bounds,
            [wall.Id],
            nodes.Select(node => node.Id).ToArray(),
            [edge.Id],
            wall.SourcePrimitiveIds,
            wall.DrawingLength,
            Confidence.High,
            ["synthetic wall with anchored opening cutout"]);
        var assessment = new WallEvidenceWallAssessment(
            wall.Id,
            wall.PageNumber,
            wall.Bounds,
            WallEvidenceCategory.StrongWallBody,
            Confidence.High,
            PlacementReady: true,
            RequiresReview: false,
            RejectedAsNoise: false,
            wall.SourcePrimitiveIds,
            ["synthetic accepted wall body evidence"])
        {
            Decision = WallEvidenceDecision.Accept
        };
        var startParameter = 0.4;
        var endParameter = 0.6;
        var centerParameter = (startParameter + endParameter) / 2.0;
        var startPoint = wall.CenterLine.PointAt(startParameter);
        var endPoint = wall.CenterLine.PointAt(endParameter);
        var openingLine = new PlanLineSegment(startPoint, endPoint);
        var alongVector = wall.CenterLine.Vector.Normalize();
        var normalVector = new PlanVector(-alongVector.Y, alongVector.X);
        var depth = 8.0;
        var footprint = openingLine.Bounds.Inflate(depth / 2.0);
        var opening = new OpeningCandidate(
            "cutout-door",
            1,
            OpeningType.Door,
            footprint,
            Confidence.High)
        {
            WallId = wall.Id,
            HostWallIds = [wall.Id],
            CenterLine = openingLine,
            Orientation = OpeningOrientation.Horizontal,
            Operation = OpeningOperation.Hinged,
            Placement = new OpeningPlacement(
                wall.Id,
                [wall.Id],
                wall.CenterLine,
                startPoint,
                endPoint,
                startParameter * wall.DrawingLength,
                endParameter * wall.DrawingLength,
                centerParameter * wall.DrawingLength,
                openingLine.Length,
                footprint,
                [
                    new PlanPoint(footprint.Left, footprint.Top),
                    new PlanPoint(footprint.Right, footprint.Top),
                    new PlanPoint(footprint.Right, footprint.Bottom),
                    new PlanPoint(footprint.Left, footprint.Bottom)
                ],
                new PlanLineSegment(startPoint, startPoint.Translate(normalVector.X * depth, normalVector.Y * depth)),
                new PlanLineSegment(endPoint, endPoint.Translate(normalVector.X * depth, normalVector.Y * depth)),
                depth,
                null,
                null,
                null,
                null,
                null,
                startParameter,
                endParameter,
                centerParameter,
                alongVector,
                normalVector,
                0,
                Confidence.High,
                ["synthetic opening placement for SVG cutout test"]),
            SourcePrimitiveIds = ["cutout-door-source"],
            Evidence = ["synthetic anchored door cutout"]
        };
        var now = DateTimeOffset.UtcNow;

        return new PlanScanResult(
            new PlanDocument(
                "opening-cutout-placement-review",
                [
                    new PlanPage(
                        1,
                        new PlanSize(360, 240),
                        [
                            WallLine(wall.Id, wall.CenterLine.Start, wall.CenterLine.End)
                        ])
                ])
            {
                Metadata = new PlanMetadata
                {
                    SourceName = "opening-cutout-placement-review.pdf",
                    SourcePath = @"C:\plans\opening-cutout-placement-review.pdf",
                    Properties = new Dictionary<string, string>
                    {
                        ["format"] = "pdf",
                        ["loader"] = "synthetic",
                        ["sourceKind"] = PlanSourceKind.Pdf.ToString(),
                        ["effectiveSourceKind"] = PlanSourceKind.Pdf.ToString()
                    }
                }
            },
            PlanLayerAnalysis.Empty,
            PlanCalibration.Empty,
            MeasurementConsistencyReport.Empty,
            Array.Empty<TitleBlockAnalysis>(),
            Array.Empty<DimensionAnnotation>(),
            Array.Empty<PlanAnnotationBlock>(),
            Array.Empty<GridAxis>(),
            Array.Empty<GridBaySpacing>(),
            Array.Empty<SheetRegion>(),
            Array.Empty<SurfacePatternCandidate>(),
            [wall],
            new WallGraph(nodes, [edge], [component]),
            Array.Empty<RoomRegion>(),
            RoomAdjacencyGraph.Empty,
            [opening],
            Array.Empty<ObjectCandidate>(),
            Array.Empty<ObjectCandidateGroup>(),
            Array.Empty<ObjectAggregate>(),
            new PipelineDiagnostics(
                now,
                now,
                Array.Empty<PipelineStageReport>(),
                Array.Empty<PlanDiagnostic>()))
        {
            WallEvidenceMap = new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                [assessment],
                1,
                0)
        };
    }

    private static PlanScanResult WithReviewOnlyWallAssessment(
        PlanScanResult result,
        string wallId)
    {
        var wall = result.Walls.Single(item => item.Id == wallId);
        var assessment = new WallEvidenceWallAssessment(
            wall.Id,
            wall.PageNumber,
            wall.Bounds,
            WallEvidenceCategory.SurfacePatternDetail,
            Confidence.Medium,
            PlacementReady: false,
            RequiresReview: true,
            RejectedAsNoise: false,
            wall.SourcePrimitiveIds,
            ["synthetic review-only detail wall should not be placement-ready"])
        {
            Decision = WallEvidenceDecision.Review
        };

        return result with
        {
            WallEvidenceMap = new WallEvidenceMap(
                result.WallEvidenceMap.Segments,
                result.WallEvidenceMap.Bands,
                result.WallEvidenceMap.WallAssessments.Append(assessment).ToArray(),
                result.WallEvidenceMap.SourceCandidateWallCount,
                result.WallEvidenceMap.RecoveredCandidateWallCount)
        };
    }

    private static PlanScanResult WithPlacementReadyIsolatedFragment(PlanScanResult result)
    {
        var wall = SyntheticWall("isolated-clean-fragment", 360, 220, 386, 220);
        var fromNode = SyntheticNode("node-isolated-fragment-a", 360, 220, WallNodeKind.Endpoint);
        var toNode = SyntheticNode("node-isolated-fragment-b", 386, 220, WallNodeKind.Endpoint);
        var edge = new WallEdge(
            "edge-isolated-fragment",
            1,
            fromNode.Id,
            toNode.Id,
            wall.Id,
            Confidence.High);
        var component = new WallGraphComponent(
            "component-isolated-fragment",
            1,
            WallGraphComponentKind.IsolatedFragment,
            wall.Bounds,
            [wall.Id],
            [fromNode.Id, toNode.Id],
            [edge.Id],
            wall.SourcePrimitiveIds,
            wall.DrawingLength,
            Confidence.Medium,
            ["synthetic isolated fragment retained for review/opening support"],
            ExcludedFromStructuralTopology: false);
        var assessment = new WallEvidenceWallAssessment(
            wall.Id,
            wall.PageNumber,
            wall.Bounds,
            WallEvidenceCategory.StrongWallBody,
            Confidence.High,
            PlacementReady: true,
            RequiresReview: false,
            RejectedAsNoise: false,
            wall.SourcePrimitiveIds,
            ["synthetic accepted wall evidence"])
        {
            Decision = WallEvidenceDecision.Accept
        };

        return result with
        {
            Walls = result.Walls.Append(wall).ToArray(),
            WallGraph = new WallGraph(
                result.WallGraph.Nodes.Concat(new[] { fromNode, toNode }).ToArray(),
                result.WallGraph.Edges.Append(edge).ToArray(),
                result.WallGraph.Components.Append(component).ToArray(),
                result.WallGraph.RepairCandidates),
            WallEvidenceMap = new WallEvidenceMap(
                result.WallEvidenceMap.Segments,
                result.WallEvidenceMap.Bands,
                result.WallEvidenceMap.WallAssessments.Append(assessment).ToArray(),
                result.WallEvidenceMap.SourceCandidateWallCount,
                result.WallEvidenceMap.RecoveredCandidateWallCount)
        };
    }

    private static PlanScanResult WithWallGraphRepairCandidate(PlanScanResult result)
    {
        var candidate = new WallGraphRepairCandidate(
            "repair-high-gap",
            1,
            WallGraphRepairCandidateKind.EndpointToWall,
            WallGraphRepairAction.SnapEndpointToWall,
            WallGraphRepairSeverity.High,
            WallGraphRepairImportImpact.TopologyImportBlocked,
            WallGraphRepairApplicability.ManualCorrectionRecommended,
            "node-right",
            new PlanPoint(300, 100),
            new PlanPoint(300, 118),
            null,
            "detail-tooth-4",
            GapDistance: 18,
            SafeSnapDistance: 9,
            ReviewDistanceLimit: 18,
            ExcessDistanceBeyondSafeSnap: 9,
            new PlanLineSegment(new PlanPoint(300, 100), new PlanPoint(300, 118)),
            new PlanRect(298, 98, 4, 22),
            ["detail-host", "detail-tooth-4"],
            ["detail-host", "detail-tooth-4"],
            Confidence.High,
            RequiresReview: true,
            ["synthetic high-severity wall graph repair candidate"]);

        return result with
        {
            WallGraph = new WallGraph(
                result.WallGraph.Nodes,
                result.WallGraph.Edges,
                result.WallGraph.Components,
                result.WallGraph.RepairCandidates.Append(candidate).ToArray())
        };
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

    private static WallSegment SyntheticWall(string id, double x1, double y1, double x2, double y2) =>
        new(
            id,
            1,
            new PlanLineSegment(new PlanPoint(x1, y1), new PlanPoint(x2, y2)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = [id],
            Evidence = ["synthetic wall"]
        };

    private static WallNode SyntheticNode(string id, double x, double y, WallNodeKind kind) =>
        new(id, 1, new PlanPoint(x, y), kind, 2, Array.Empty<string>(), Confidence.High, ["synthetic wall node"]);

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
