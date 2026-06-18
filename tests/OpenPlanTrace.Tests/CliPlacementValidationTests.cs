using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class CliPlacementValidationTests
{
    [Fact]
    public async Task ValidateScanDeep_AcceptsGeneratedArtifactInventory()
    {
        using var workspace = TestWorkspace.Create();
        var scanPath = workspace.Write("scan.json", await CreateGeneratedScanJsonAsync());
        var validationPath = workspace.PathFor("validation.json");

        var exitCode = await global::OpenPlanTraceCli.RunAsync(new[]
        {
            "validate",
            scanPath,
            "--kind",
            "scan",
            "--deep",
            "--json",
            validationPath,
            "--compact-json"
        });

        Assert.Equal(0, exitCode);
        using var validation = JsonDocument.Parse(await File.ReadAllTextAsync(validationPath));
        Assert.True(validation.RootElement.GetProperty("valid").GetBoolean());
        Assert.Contains(
            validation.RootElement.GetProperty("messages").EnumerateArray(),
            message => message.GetProperty("message").GetString()?.Contains("Scan deep validation checked final artifact inventory", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ValidateScanDeep_RejectsStaleArtifactInventoryCount()
    {
        using var workspace = TestWorkspace.Create();
        var scanJson = await CreateGeneratedScanJsonAsync();
        using var scan = JsonDocument.Parse(scanJson);
        var wallsCount = scan.RootElement
            .GetProperty("diagnostics")
            .GetProperty("artifactInventory")
            .EnumerateArray()
            .First(item => item.GetProperty("artifact").GetString() == nameof(PlanArtifactKind.Walls))
            .GetProperty("count")
            .GetInt32();
        var staleJson = scanJson.Replace(
            $"\"artifact\":\"{nameof(PlanArtifactKind.Walls)}\",\"count\":{wallsCount}",
            $"\"artifact\":\"{nameof(PlanArtifactKind.Walls)}\",\"count\":{wallsCount + 10}",
            StringComparison.Ordinal);
        Assert.NotEqual(scanJson, staleJson);

        var scanPath = workspace.Write("scan.json", staleJson);
        var validationPath = workspace.PathFor("validation.json");

        var exitCode = await global::OpenPlanTraceCli.RunAsync(new[]
        {
            "validate",
            scanPath,
            "--kind",
            "scan",
            "--deep",
            "--json",
            validationPath,
            "--compact-json"
        });

        Assert.Equal(1, exitCode);
        using var validation = JsonDocument.Parse(await File.ReadAllTextAsync(validationPath));
        Assert.False(validation.RootElement.GetProperty("valid").GetBoolean());

        var messages = validation.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Select(message => message.GetProperty("message").GetString() ?? string.Empty)
            .ToArray();
        Assert.Contains(messages, message => message.Contains("artifact 'Walls' count should be", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidatePlacementDeep_AcceptsCoherentPlacementPacket()
    {
        using var workspace = TestWorkspace.Create();
        var placementPath = workspace.Write("placement.json", CreatePlacementJson());
        var validationPath = workspace.PathFor("validation.json");

        var exitCode = await global::OpenPlanTraceCli.RunAsync(new[]
        {
            "validate",
            placementPath,
            "--kind",
            "placement",
            "--deep",
            "--json",
            validationPath,
            "--compact-json"
        });

        Assert.Equal(0, exitCode);
        using var validation = JsonDocument.Parse(await File.ReadAllTextAsync(validationPath));
        Assert.True(validation.RootElement.GetProperty("valid").GetBoolean());
        Assert.Contains(
            validation.RootElement.GetProperty("messages").EnumerateArray(),
            message => message.GetProperty("message").GetString()?.Contains("Placement deep validation checked", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ValidatePlacementDeep_AcceptsSurfacePatternWallOverlapIssueLinkedToWallReliability()
    {
        using var workspace = TestWorkspace.Create();
        var placementPath = workspace.Write(
            "placement.json",
            CreatePlacementJson(
                includeSurfaceOverlapIssue: true,
                markSurfaceOverlapWallReview: true));
        var validationPath = workspace.PathFor("validation.json");

        var exitCode = await global::OpenPlanTraceCli.RunAsync(new[]
        {
            "validate",
            placementPath,
            "--kind",
            "placement",
            "--deep",
            "--json",
            validationPath,
            "--compact-json"
        });

        Assert.Equal(0, exitCode);
        using var validation = JsonDocument.Parse(await File.ReadAllTextAsync(validationPath));
        Assert.True(validation.RootElement.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task ValidatePlacementDeep_RejectsSurfacePatternWallOverlapIssueWithoutWallReliabilityReason()
    {
        using var workspace = TestWorkspace.Create();
        var placementPath = workspace.Write(
            "placement.json",
            CreatePlacementJson(includeSurfaceOverlapIssue: true));
        var validationPath = workspace.PathFor("validation.json");

        var exitCode = await global::OpenPlanTraceCli.RunAsync(new[]
        {
            "validate",
            placementPath,
            "--kind",
            "placement",
            "--deep",
            "--json",
            validationPath,
            "--compact-json"
        });

        Assert.Equal(1, exitCode);
        using var validation = JsonDocument.Parse(await File.ReadAllTextAsync(validationPath));
        Assert.False(validation.RootElement.GetProperty("valid").GetBoolean());

        var messages = validation.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Select(message => message.GetProperty("message").GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains(messages, message => message.Contains("reliability.requiresReview must be true", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("reliability.reasons must include the surface/detail pattern overlap evidence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidatePlacementDeep_RejectsBrokenOpeningPlacementReferencesAndOffsets()
    {
        using var workspace = TestWorkspace.Create();
        var placementPath = workspace.Write(
            "placement.json",
            CreatePlacementJson(
                openingHostWallId: "missing-wall",
                placementHostWallId: "missing-wall",
                endOffset: 22,
                length: 30));
        var validationPath = workspace.PathFor("validation.json");

        var exitCode = await global::OpenPlanTraceCli.RunAsync(new[]
        {
            "validate",
            placementPath,
            "--kind",
            "placement",
            "--deep",
            "--json",
            validationPath,
            "--compact-json"
        });

        Assert.Equal(1, exitCode);
        using var validation = JsonDocument.Parse(await File.ReadAllTextAsync(validationPath));
        Assert.False(validation.RootElement.GetProperty("valid").GetBoolean());

        var messages = validation.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Select(message => message.GetProperty("message").GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains(messages, message => message.Contains("hostWallIds references missing wall 'missing-wall'", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("placement hostWallId references missing wall 'missing-wall'", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("lengthDrawingUnits must match", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidatePlacementDeep_RejectsBrokenOpeningFootprintGeometry()
    {
        using var workspace = TestWorkspace.Create();
        var placementPath = workspace.Write(
            "placement.json",
            CreatePlacementJson().Replace(
                "\"depthDrawingUnits\": 4",
                "\"depthDrawingUnits\": 2",
                StringComparison.Ordinal));
        var validationPath = workspace.PathFor("validation.json");

        var exitCode = await global::OpenPlanTraceCli.RunAsync(new[]
        {
            "validate",
            placementPath,
            "--kind",
            "placement",
            "--deep",
            "--json",
            validationPath,
            "--compact-json"
        });

        Assert.Equal(1, exitCode);
        using var validation = JsonDocument.Parse(await File.ReadAllTextAsync(validationPath));
        Assert.False(validation.RootElement.GetProperty("valid").GetBoolean());

        var messages = validation.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Select(message => message.GetProperty("message").GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains(messages, message => message.Contains("depthDrawingUnits must match start/end jamb line lengths", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidatePlacementDeep_RejectsBrokenRoutingReferences()
    {
        using var workspace = TestWorkspace.Create();
        var placementPath = workspace.Write(
            "placement.json",
            CreatePlacementJson(
                routingPassageSourceId: "missing-opening",
                suppressedByAggregateId: "missing-aggregate",
                replacementRoutingObstacleId: "missing-routing-obstacle"));
        var validationPath = workspace.PathFor("validation.json");

        var exitCode = await global::OpenPlanTraceCli.RunAsync(new[]
        {
            "validate",
            placementPath,
            "--kind",
            "placement",
            "--deep",
            "--json",
            validationPath,
            "--compact-json"
        });

        Assert.Equal(1, exitCode);
        using var validation = JsonDocument.Parse(await File.ReadAllTextAsync(validationPath));
        Assert.False(validation.RootElement.GetProperty("valid").GetBoolean());

        var messages = validation.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Select(message => message.GetProperty("message").GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains(messages, message => message.Contains("sourceId references missing opening 'missing-opening'", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("suppressedByAggregateId references missing object aggregate 'missing-aggregate'", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("replacementRoutingObstacleId references missing routing obstacle 'missing-routing-obstacle'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidatePlacementDeep_RejectsRoutingPassageThatClaimsReadinessWithBadPlacement()
    {
        using var workspace = TestWorkspace.Create();
        var placementPath = workspace.Write(
            "placement.json",
            CreatePlacementJson(endOffset: 22, length: 30));
        var validationPath = workspace.PathFor("validation.json");

        var exitCode = await global::OpenPlanTraceCli.RunAsync(new[]
        {
            "validate",
            placementPath,
            "--kind",
            "placement",
            "--deep",
            "--json",
            validationPath,
            "--compact-json"
        });

        Assert.Equal(1, exitCode);
        using var validation = JsonDocument.Parse(await File.ReadAllTextAsync(validationPath));
        Assert.False(validation.RootElement.GetProperty("valid").GetBoolean());

        var messages = validation.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Select(message => message.GetProperty("message").GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains(messages, message => message.Contains("routing passage readyForCoordinatePlacement is true but placement failed coordinate-readiness checks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidatePlacementDeep_RejectsMismatchedCoordinateFrameTransform()
    {
        using var workspace = TestWorkspace.Create();
        var placementPath = workspace.Write(
            "placement.json",
            CreatePlacementJson().Replace(
                "\"normalizedToPageTransform\": [200, 0, 0, 120, 0, 0]",
                "\"normalizedToPageTransform\": [199, 0, 0, 120, 0, 0]",
                StringComparison.Ordinal));
        var validationPath = workspace.PathFor("validation.json");

        var exitCode = await global::OpenPlanTraceCli.RunAsync(new[]
        {
            "validate",
            placementPath,
            "--kind",
            "placement",
            "--deep",
            "--json",
            validationPath,
            "--compact-json"
        });

        Assert.Equal(1, exitCode);
        using var validation = JsonDocument.Parse(await File.ReadAllTextAsync(validationPath));
        Assert.False(validation.RootElement.GetProperty("valid").GetBoolean());

        var messages = validation.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Select(message => message.GetProperty("message").GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains(messages, message => message.Contains("normalizedToPageTransform[0] does not match", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidatePlacementDeep_RejectsMismatchedMetricObjectCoordinates()
    {
        using var workspace = TestWorkspace.Create();
        var placementPath = workspace.Write(
            "placement.json",
            CreatePlacementJson().Replace(
                "\"boundsMillimeters\": { \"x\": 800, \"y\": 600, \"width\": 150, \"height\": 100 }",
                "\"boundsMillimeters\": { \"x\": 801, \"y\": 600, \"width\": 150, \"height\": 100 }",
                StringComparison.Ordinal));
        var validationPath = workspace.PathFor("validation.json");

        var exitCode = await global::OpenPlanTraceCli.RunAsync(new[]
        {
            "validate",
            placementPath,
            "--kind",
            "placement",
            "--deep",
            "--json",
            validationPath,
            "--compact-json"
        });

        Assert.Equal(1, exitCode);
        using var validation = JsonDocument.Parse(await File.ReadAllTextAsync(validationPath));
        Assert.False(validation.RootElement.GetProperty("valid").GetBoolean());

        var messages = validation.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Select(message => message.GetProperty("message").GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains(messages, message => message.Contains("boundsMillimeters.x must equal drawing coordinate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidatePlacement_RejectsMismatchedDocumentProvenance()
    {
        using var workspace = TestWorkspace.Create();
        var placementPath = workspace.Write(
            "placement.json",
            CreatePlacementJson().Replace(
                "\"sourceFormat\": \"pdf\"",
                "\"sourceFormat\": \"dxf\"",
                StringComparison.Ordinal));
        var validationPath = workspace.PathFor("validation.json");

        var exitCode = await global::OpenPlanTraceCli.RunAsync(new[]
        {
            "validate",
            placementPath,
            "--kind",
            "placement",
            "--json",
            validationPath,
            "--compact-json"
        });

        Assert.Equal(1, exitCode);
        using var validation = JsonDocument.Parse(await File.ReadAllTextAsync(validationPath));
        Assert.False(validation.RootElement.GetProperty("valid").GetBoolean());

        var messages = validation.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Select(message => message.GetProperty("message").GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains(messages, message => message.Contains("sourceFormat must match properties['format']", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidatePlacement_RejectsStaleSummaryCounts()
    {
        using var workspace = TestWorkspace.Create();
        var placementPath = workspace.Write(
            "placement.json",
            CreatePlacementJson().Replace(
                "\"wallCount\": 1",
                "\"wallCount\": 99",
                StringComparison.Ordinal));
        var validationPath = workspace.PathFor("validation.json");

        var exitCode = await global::OpenPlanTraceCli.RunAsync(new[]
        {
            "validate",
            placementPath,
            "--kind",
            "placement",
            "--json",
            validationPath,
            "--compact-json"
        });

        Assert.Equal(1, exitCode);
        using var validation = JsonDocument.Parse(await File.ReadAllTextAsync(validationPath));
        Assert.False(validation.RootElement.GetProperty("valid").GetBoolean());

        var messages = validation.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Select(message => message.GetProperty("message").GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains(messages, message => message.Contains("Placement summary wallCount should be 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidatePlacement_RejectsImpossibleImportReadiness()
    {
        using var workspace = TestWorkspace.Create();
        var placementPath = workspace.Write(
            "placement.json",
            CreatePlacementJson().Replace(
                "\"readyForGeometryImport\": true",
                "\"readyForGeometryImport\": false",
                StringComparison.Ordinal));
        var validationPath = workspace.PathFor("validation.json");

        var exitCode = await global::OpenPlanTraceCli.RunAsync(new[]
        {
            "validate",
            placementPath,
            "--kind",
            "placement",
            "--json",
            validationPath,
            "--compact-json"
        });

        Assert.Equal(1, exitCode);
        using var validation = JsonDocument.Parse(await File.ReadAllTextAsync(validationPath));
        Assert.False(validation.RootElement.GetProperty("valid").GetBoolean());

        var messages = validation.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Select(message => message.GetProperty("message").GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains(messages, message => message.Contains("readyForMetricImport cannot be true unless readyForGeometryImport is true", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("readyForRoutingImport cannot be true unless readyForGeometryImport is true", StringComparison.Ordinal));
    }

    private static string CreatePlacementJson(
        string openingHostWallId = "wall-1",
        string placementHostWallId = "wall-1",
        double startOffset = 10,
        double endOffset = 40,
        double centerOffset = 25,
        double length = 30,
        string routingPassageSourceId = "opening-1",
        string suppressedByAggregateId = "aggregate-1",
        string replacementRoutingObstacleId = "routing-obstacle:aggregate-1",
        bool includeSurfaceOverlapIssue = false,
        string surfaceOverlapWallId = "wall-1",
        bool markSurfaceOverlapWallReview = false) =>
        $$"""
        {
          "schemaVersion": "{{PlanPlacementExport.CurrentSchemaVersion}}",
          "generatedAt": "2026-06-11T00:00:00Z",
          "scanSchemaVersion": "{{PlanTraceExport.CurrentSchemaVersion}}",
          "document": {
            "id": "deep-placement-test",
            "sourceName": "deep-placement-test.pdf",
            "sourcePath": null,
            "sourceFormat": "pdf",
            "loader": "PDF/PdfPig",
            "sourceKind": "Pdf",
            "effectiveSourceKind": "Pdf",
            "clipboardContentKind": null,
            "fileExtension": ".pdf",
            "contentType": "application/pdf",
            "isDwgDerived": false,
            "dwgConversion": null,
            "dwgConverter": null,
            "dwgIntermediateFormat": null,
            "dwgIntermediateLoader": null,
            "rasterAdapter": null,
            "rasterExtractor": null,
            "rasterExtractorVersion": null,
            "rasterModelName": null,
            "rasterModelVersion": null,
            "properties": {
              "format": "pdf",
              "loader": "PDF/PdfPig",
              "sourceKind": "Pdf",
              "effectiveSourceKind": "Pdf",
              "fileExtension": ".pdf",
              "contentType": "application/pdf"
            }
          },
          "coordinateSystem": {
            "coordinateSpace": "OpenPlanTracePageCoordinates",
            "unit": "drawing-unit",
            "origin": "TopLeft",
            "xAxisDirection": "Right",
            "yAxisDirection": "Down",
            "geometryBasis": "PDF/DXF page coordinate space after OpenPlanTrace normalization",
            "coordinateOrder": "x,y",
            "boundsKind": "x,y,width,height for rectangles; start/end for lines; ordered point arrays for polygons",
            "precision": "double",
            "realWorldUnit": "Millimeter",
            "millimetersPerDrawingUnit": 10,
            "note": "Coordinates are page-local drawing units.",
            "pageFrames": [
              {
                "pageNumber": 1,
                "width": 200,
                "height": 120,
                "bounds": { "x": 0, "y": 0, "width": 200, "height": 120 },
                "pageToNormalizedTransform": [0.005, 0, 0, 0.0083333333, 0, 0],
                "normalizedToPageTransform": [200, 0, 0, 120, 0, 0]
              }
            ]
          },
          "calibration": {
            "drawingUnit": "DrawingUnit",
            "realWorldUnit": "Millimeter",
            "scaleRatio": 100,
            "millimetersPerDrawingUnit": 10,
            "hasReliableMeasurementScale": true,
            "metricCoordinateStatus": "Available",
            "evidenceCount": 1,
            "scaleGroupCount": 1,
            "measurementCheckedCount": 1,
            "measurementConsistentCount": 1,
            "measurementOutlierCount": 0,
            "measurementConfidence": 0.9,
            "scaleGroups": [],
            "evidence": ["test calibration"]
          },
          "qualityGate": {
            "coordinateTrust": "Usable",
            "metricTrust": "Calibrated",
            "readyForCoordinatePlacement": true,
            "readyForMetricPlacement": true,
            "qualityGrade": "Strong",
            "qualityConfidence": 0.95,
            "requiresReview": false,
            "hasReliableCalibration": true,
            "diagnosticWarningCount": 0,
            "diagnosticErrorCount": 0,
            "evidence": ["test quality gate"]
          },
          "summary": {
            "pageCount": 1,
            "mainFloorplanRegionCount": 0,
            "surfacePatternCount": 0,
            "wallCount": 1,
            "structuralWallCount": 1,
            "excludedWallCount": 0,
            "placementReadyWallCount": 1,
            "placementOmittedWallCount": 0,
            "wallTopologySpanCount": 0,
            "wallSolidSpanCount": 0,
            "wallPlacementOmissionCounts": {},
            "roomCount": 1,
            "openingCount": 1,
            "anchoredOpeningCount": 1,
            "unanchoredOpeningCount": 0,
            "objectAggregateCount": 1,
            "wallGraphRepairCandidateCount": 0,
            "suppressedChildObjectCount": 1,
            "routingBarrierCount": 1,
            "routingPassageCount": 1,
            "routingObstacleCount": 1,
            "routingRoomUseHintCount": 1,
            "routingSuppressedObjectCount": 1,
            "routingItemCount": 5,
            "totalPlacementEntityCount": 9,
            "reliabilityTrackedEntityCount": 4,
            "coordinateReadyEntityCount": 4,
            "metricReadyEntityCount": 4,
            "reviewRequiredEntityCount": {{(markSurfaceOverlapWallReview ? 1 : 0)}},
            "coordinateReadyRatio": 1,
            "metricReadyRatio": 1,
            "issueCount": {{(includeSurfaceOverlapIssue ? 1 : 0)}},
            "infoIssueCount": 0,
            "warningIssueCount": {{(includeSurfaceOverlapIssue ? 1 : 0)}},
            "errorIssueCount": 0,
            "sourcePrimitiveReferenceCount": 7,
            "uniqueSourcePrimitiveReferenceCount": 3,
            "importReadiness": {
              "grade": "{{(includeSurfaceOverlapIssue ? "ReviewRequired" : "Strong")}}",
              "score": 0.95,
              "readyForGeometryImport": true,
              "readyForMetricImport": true,
              "readyForRoutingImport": true,
              "requiresReview": {{JsonBool(includeSurfaceOverlapIssue)}},
              "blockingIssueCodes": [],
              "reviewIssueCodes": {{SurfaceOverlapReviewIssueCodesJson(includeSurfaceOverlapIssue)}},
              "recommendedActions": {{SurfaceOverlapRecommendedActionsJson(includeSurfaceOverlapIssue)}},
              "evidence": [
                "import readiness grade {{(includeSurfaceOverlapIssue ? "ReviewRequired" : "Strong")}} with score 0.95",
                "geometry import ready: True",
                "metric import ready: True",
                "routing import ready: True"
              ]
            },
            "pageSummaries": [
              {
                "pageNumber": 1,
                "pageBounds": { "x": 0, "y": 0, "width": 200, "height": 120 },
                "mainFloorplanBounds": null,
                "detectionBounds": { "x": 20, "y": 36, "width": 100, "height": 54 },
                "detectionBoundsMillimeters": { "x": 200, "y": 360, "width": 1000, "height": 540 },
                "surfacePatternCount": 0,
                "wallCount": 1,
                "structuralWallCount": 1,
                "excludedWallCount": 0,
                "placementReadyWallCount": 1,
                "placementOmittedWallCount": 0,
                "wallTopologySpanCount": 0,
                "wallSolidSpanCount": 0,
                "wallPlacementOmissionCounts": {},
                "roomCount": 1,
                "openingCount": 1,
                "anchoredOpeningCount": 1,
                "unanchoredOpeningCount": 0,
                "objectAggregateCount": 1,
                "wallGraphRepairCandidateCount": 0,
                "routingItemCount": 5,
                "reliabilityTrackedEntityCount": 4,
                "coordinateReadyEntityCount": 4,
                "metricReadyEntityCount": 4,
                "reviewRequiredEntityCount": {{(markSurfaceOverlapWallReview ? 1 : 0)}},
                "issueCount": {{(includeSurfaceOverlapIssue ? 1 : 0)}}
              }
            ],
            "evidence": [
              "placement summary covers 1 page(s)",
              "coordinate-ready entities 4/4",
              "metric-ready entities 4/4"
            ]
          },
          "pages": [
            {
              "pageNumber": 1,
              "width": 200,
              "height": 120,
              "bounds": { "x": 0, "y": 0, "width": 200, "height": 120 }
            }
          ],
          "surfacePatterns": [],
          "walls": [
            {
              "id": "wall-1",
              "pageNumber": 1,
              "centerLine": { "start": { "x": 20, "y": 40 }, "end": { "x": 120, "y": 40 } },
              "centerLineMillimeters": { "start": { "x": 200, "y": 400 }, "end": { "x": 1200, "y": 400 } },
              "bounds": { "x": 20, "y": 38, "width": 100, "height": 4 },
              "boundsMillimeters": { "x": 200, "y": 380, "width": 1000, "height": 40 },
              "drawingLength": 100,
              "lengthMeters": 1,
              "thicknessDrawingUnits": 4,
              "thicknessMillimeters": 40,
              "detectionKind": "Line",
              "wallComponentId": null,
              "wallComponentKind": null,
              "excludedFromStructuralTopology": false,
              "measurementScaleGroupId": null,
              "millimetersPerDrawingUnit": 10,
              "confidence": 0.9,
              "reliability": {
                "readyForCoordinatePlacement": true,
                "readyForMetricPlacement": true,
                "requiresReview": {{JsonBool(markSurfaceOverlapWallReview)}},
                "confidence": 0.9,
                "reasons": {{SurfaceOverlapWallReasonsJson(markSurfaceOverlapWallReview)}}
              },
              "wallGraphRepairCandidateIds": [],
              "sourcePrimitiveIds": ["wall-primitive-1"],
              "sourceLayers": ["A-WALL"],
              "evidence": ["test wall"]
            }
          ],
          "rooms": [
            {
              "id": "room-1",
              "pageNumber": 1,
              "bounds": { "x": 20, "y": 40, "width": 100, "height": 50 },
              "boundsMillimeters": { "x": 200, "y": 400, "width": 1000, "height": 500 },
              "center": { "x": 70, "y": 65 },
              "centerMillimeters": { "x": 700, "y": 650 },
              "boundary": [
                { "x": 20, "y": 40 },
                { "x": 120, "y": 40 },
                { "x": 120, "y": 90 },
                { "x": 20, "y": 90 }
              ],
              "boundaryMillimeters": [
                { "x": 200, "y": 400 },
                { "x": 1200, "y": 400 },
                { "x": 1200, "y": 900 },
                { "x": 200, "y": 900 }
              ],
              "wallIds": ["wall-1"],
              "drawingArea": 5000,
              "areaSquareMeters": 5,
              "measurementScaleGroupId": null,
              "millimetersPerDrawingUnit": 10,
              "label": "Test room",
              "useKind": "Unknown",
              "confidence": 0.9,
              "reliability": {
                "readyForCoordinatePlacement": true,
                "readyForMetricPlacement": true,
                "requiresReview": false,
                "confidence": 0.9,
                "reasons": []
              },
              "evidence": ["test room"]
            }
          ],
          "openings": [
            {
              "id": "opening-1",
              "pageNumber": 1,
              "type": "Door",
              "operation": "Swing",
              "orientation": "Horizontal",
              "centerLine": { "start": { "x": 30, "y": 40 }, "end": { "x": 60, "y": 40 } },
              "centerLineMillimeters": { "start": { "x": 300, "y": 400 }, "end": { "x": 600, "y": 400 } },
              "bounds": { "x": 30, "y": 36, "width": 30, "height": 8 },
              "boundsMillimeters": { "x": 300, "y": 360, "width": 300, "height": 80 },
              "drawingWidth": 30,
              "widthMillimeters": 300,
              "measurementScaleGroupId": null,
              "millimetersPerDrawingUnit": 10,
              "placementStatus": "Anchored",
              "placement": {
                "hostWallId": "{{placementHostWallId}}",
                "anchorWallIds": ["{{placementHostWallId}}"],
                "referenceLine": { "start": { "x": 20, "y": 40 }, "end": { "x": 120, "y": 40 } },
                "startPoint": { "x": 30, "y": 40 },
                "endPoint": { "x": 60, "y": 40 },
                "startOffsetDrawingUnits": {{startOffset}},
                "endOffsetDrawingUnits": {{endOffset}},
                "centerOffsetDrawingUnits": {{centerOffset}},
                "lengthDrawingUnits": {{length}},
                "footprintBounds": { "x": 30, "y": 38, "width": 30, "height": 4 },
                "footprintCorners": [
                  { "x": 30, "y": 38 },
                  { "x": 60, "y": 38 },
                  { "x": 60, "y": 42 },
                  { "x": 30, "y": 42 }
                ],
                "startJambLine": { "start": { "x": 30, "y": 38 }, "end": { "x": 30, "y": 42 } },
                "endJambLine": { "start": { "x": 60, "y": 38 }, "end": { "x": 60, "y": 42 } },
                "depthDrawingUnits": 4,
                "depthMillimeters": 40,
                "startOffsetMillimeters": 100,
                "endOffsetMillimeters": 400,
                "centerOffsetMillimeters": 250,
                "lengthMillimeters": 300,
                "hostWallStartParameter": 0.1,
                "hostWallEndParameter": 0.4,
                "hostWallCenterParameter": 0.25,
                "alongVector": { "x": 1, "y": 0 },
                "normalVector": { "x": 0, "y": 1 },
                "crossWallOffsetDrawingUnits": 0,
                "confidence": 0.9,
                "evidence": ["test placement"]
              },
              "hingeSide": "Unknown",
              "swingSide": "Unknown",
              "swingDirection": "Unknown",
              "hingePoint": null,
              "hingePointMillimeters": null,
              "hostWallIds": ["{{openingHostWallId}}"],
              "connectedRoomIds": ["room-1"],
              "connectedRoomLabels": ["Test room"],
              "connectedRoomLinks": [
                {
                  "roomId": "room-1",
                  "roomLabel": "Test room",
                  "roomUseKind": "Unknown",
                  "roomAdjacencyIds": ["room-adjacency-1"],
                  "side": "PositiveNormalSide",
                  "roomSidePoint": { "x": 45, "y": 43 },
                  "nearestBoundaryPoint": { "x": 45, "y": 40 },
                  "signedDistanceFromOpening": 25,
                  "distanceToOpening": 0,
                  "sharesHostWall": true,
                  "confidence": 0.9,
                  "evidence": ["test room side link"]
                }
              ],
              "roomAdjacencyIds": ["room-adjacency-1"],
              "confidence": 0.9,
              "reliability": {
                "readyForCoordinatePlacement": true,
                "readyForMetricPlacement": true,
                "requiresReview": false,
                "confidence": 0.9,
                "reasons": []
              },
              "sourcePrimitiveIds": ["opening-primitive-1"],
              "sourceLayers": ["A-DOOR"],
              "evidence": ["test opening"]
            }
          ],
          "objectAggregates": [
            {
              "id": "aggregate-1",
              "pageNumber": 1,
              "bounds": { "x": 80, "y": 60, "width": 15, "height": 10 },
              "boundsMillimeters": { "x": 800, "y": 600, "width": 150, "height": 100 },
              "center": { "x": 87.5, "y": 65 },
              "centerMillimeters": { "x": 875, "y": 650 },
              "millimetersPerDrawingUnit": 10,
              "category": "Furniture",
              "kind": "GenericSymbol",
              "routingInfluence": "SoftObstacle",
              "structuralInfluence": "None",
              "suppressChildObjectsForRouting": true,
              "childObjectCount": 2,
              "childObjectIds": ["object-1", "object-2"],
              "objectGroupIds": ["group-1"],
              "label": "Test aggregate",
              "roomId": "room-1",
              "roomLabel": "Test room",
              "requiresReview": false,
              "confidence": 0.8,
              "reliability": {
                "readyForCoordinatePlacement": true,
                "readyForMetricPlacement": true,
                "requiresReview": false,
                "confidence": 0.8,
                "reasons": []
              },
              "sourcePrimitiveIds": ["object-primitive-1"],
              "sourceLayers": ["A-FURN"],
              "evidence": ["test aggregate"]
            }
          ],
          "wallGraphRepairCandidates": [],
          "wallGraph": {
            "summary": {
              "nodeCount": 2,
              "edgeCount": 1,
              "componentCount": 1,
              "mainStructuralComponentCount": 1,
              "secondaryStructuralComponentCount": 0,
              "objectLikeComponentCount": 0,
              "isolatedFragmentComponentCount": 0,
              "structuralEdgeCount": 1,
              "excludedEdgeCount": 0,
              "repairCandidateCount": 0,
              "highSeverityRepairCandidateCount": 0,
              "reviewRepairCandidateCount": 0,
              "blockingRepairCandidateCount": 0
            },
            "nodes": [
              {
                "id": "node-1",
                "pageNumber": 1,
                "position": { "x": 20, "y": 40 },
                "positionMillimeters": { "x": 200, "y": 400 },
                "kind": "Endpoint",
                "degree": 1,
                "directions": ["East"],
                "confidence": 0.9,
                "evidence": ["test node"]
              },
              {
                "id": "node-2",
                "pageNumber": 1,
                "position": { "x": 120, "y": 40 },
                "positionMillimeters": { "x": 1200, "y": 400 },
                "kind": "Endpoint",
                "degree": 1,
                "directions": ["West"],
                "confidence": 0.9,
                "evidence": ["test node"]
              }
            ],
            "edges": [
              {
                "id": "edge-1",
                "pageNumber": 1,
                "fromNodeId": "node-1",
                "toNodeId": "node-2",
                "wallId": "wall-1",
                "wallComponentId": null,
                "wallComponentKind": "MainStructural",
                "excludedFromStructuralTopology": false,
                "centerLine": { "start": { "x": 20, "y": 40 }, "end": { "x": 120, "y": 40 } },
                "centerLineMillimeters": { "start": { "x": 200, "y": 400 }, "end": { "x": 1200, "y": 400 } },
                "bounds": { "x": 20, "y": 38, "width": 100, "height": 4 },
                "boundsMillimeters": { "x": 200, "y": 380, "width": 1000, "height": 40 },
                "drawingLength": 100,
                "lengthMeters": 1,
                "thicknessDrawingUnits": 4,
                "thicknessMillimeters": 40,
                "millimetersPerDrawingUnit": 10,
                "confidence": 0.9,
                "sourcePrimitiveIds": ["wall-primitive-1"],
                "sourceLayers": ["A-WALL"],
                "evidence": ["test wall graph edge"]
              }
            ],
            "components": [
              {
                "id": "component-1",
                "pageNumber": 1,
                "kind": "MainStructural",
                "bounds": { "x": 20, "y": 38, "width": 100, "height": 4 },
                "boundsMillimeters": { "x": 200, "y": 380, "width": 1000, "height": 40 },
                "wallIds": ["wall-1"],
                "nodeIds": ["node-1", "node-2"],
                "edgeIds": ["edge-1"],
                "sourcePrimitiveIds": ["wall-primitive-1"],
                "sourceLayers": ["A-WALL"],
                "wallCount": 1,
                "nodeCount": 2,
                "edgeCount": 1,
                "drawingLength": 100,
                "lengthMeters": 1,
                "confidence": 0.9,
                "excludedFromStructuralTopology": false,
                "evidence": ["test component"]
              }
            ],
            "repairCandidateIds": [],
            "evidence": ["test wall graph"]
          },
          "routingLayer": {
            "barriers": [
              {
                "id": "routing-barrier:wall-1",
                "pageNumber": 1,
                "sourceId": "wall-1",
                "sourceKind": "Wall",
                "centerLine": { "start": { "x": 20, "y": 40 }, "end": { "x": 120, "y": 40 } },
                "centerLineMillimeters": { "start": { "x": 200, "y": 400 }, "end": { "x": 1200, "y": 400 } },
                "bounds": { "x": 20, "y": 38, "width": 100, "height": 4 },
                "boundsMillimeters": { "x": 200, "y": 380, "width": 1000, "height": 40 },
                "thickness": 4,
                "drawingLength": 100,
                "lengthMeters": 1,
                "thicknessMillimeters": 40,
                "measurementScaleGroupId": null,
                "millimetersPerDrawingUnit": 10,
                "wallComponentId": null,
                "wallComponentKind": null,
                "excludedFromStructuralTopology": false,
                "confidence": 0.9,
                "sourcePrimitiveIds": ["wall-primitive-1"],
                "sourceLayers": ["A-WALL"],
                "evidence": ["test routing barrier"]
              }
            ],
            "passages": [
              {
                "id": "routing-passage:opening-1",
                "pageNumber": 1,
                "sourceId": "{{routingPassageSourceId}}",
                "sourceKind": "Opening",
                "type": "Door",
                "operation": "Swing",
                "orientation": "Horizontal",
                "centerLine": { "start": { "x": 30, "y": 40 }, "end": { "x": 60, "y": 40 } },
                "centerLineMillimeters": { "start": { "x": 300, "y": 400 }, "end": { "x": 600, "y": 400 } },
                "bounds": { "x": 30, "y": 36, "width": 30, "height": 8 },
                "boundsMillimeters": { "x": 300, "y": 360, "width": 300, "height": 80 },
                "drawingWidth": 30,
                "widthMillimeters": 300,
                "measurementScaleGroupId": null,
                "millimetersPerDrawingUnit": 10,
                "hostWallIds": ["{{openingHostWallId}}"],
                "connectedRoomIds": ["room-1"],
                "connectedRoomLabels": ["Test room"],
                "connectedRoomLinks": [
                  {
                    "roomId": "room-1",
                    "roomLabel": "Test room",
                    "roomUseKind": "Unknown",
                    "roomAdjacencyIds": ["room-adjacency-1"],
                    "side": "PositiveNormalSide",
                    "roomSidePoint": { "x": 45, "y": 43 },
                    "nearestBoundaryPoint": { "x": 45, "y": 40 },
                    "signedDistanceFromOpening": 25,
                    "distanceToOpening": 0,
                    "sharesHostWall": true,
                    "confidence": 0.9,
                    "evidence": ["test routing passage room side link"]
                  }
                ],
                "roomAdjacencyIds": ["room-adjacency-1"],
                "placement": {
                  "hostWallId": "{{placementHostWallId}}",
                  "anchorWallIds": ["{{placementHostWallId}}"],
                  "referenceLine": { "start": { "x": 20, "y": 40 }, "end": { "x": 120, "y": 40 } },
                  "startPoint": { "x": 30, "y": 40 },
                  "endPoint": { "x": 60, "y": 40 },
                  "startOffsetDrawingUnits": {{startOffset}},
                  "endOffsetDrawingUnits": {{endOffset}},
                  "centerOffsetDrawingUnits": {{centerOffset}},
                  "lengthDrawingUnits": {{length}},
                  "footprintBounds": { "x": 30, "y": 38, "width": 30, "height": 4 },
                  "footprintCorners": [
                    { "x": 30, "y": 38 },
                    { "x": 60, "y": 38 },
                    { "x": 60, "y": 42 },
                    { "x": 30, "y": 42 }
                  ],
                  "startJambLine": { "start": { "x": 30, "y": 38 }, "end": { "x": 30, "y": 42 } },
                  "endJambLine": { "start": { "x": 60, "y": 38 }, "end": { "x": 60, "y": 42 } },
                  "depthDrawingUnits": 4,
                  "depthMillimeters": 40,
                  "startOffsetMillimeters": 100,
                  "endOffsetMillimeters": 400,
                  "centerOffsetMillimeters": 250,
                  "lengthMillimeters": 300,
                  "hostWallStartParameter": 0.1,
                  "hostWallEndParameter": 0.4,
                  "hostWallCenterParameter": 0.25,
                  "alongVector": { "x": 1, "y": 0 },
                  "normalVector": { "x": 0, "y": 1 },
                  "crossWallOffsetDrawingUnits": 0,
                  "confidence": 0.9,
                  "evidence": ["test routing passage placement"]
                },
                "placementStatus": "Anchored",
                "readyForCoordinatePlacement": true,
                "requiresReview": false,
                "reviewReasons": [],
                "confidence": 0.9,
                "sourcePrimitiveIds": ["opening-primitive-1"],
                "sourceLayers": ["A-DOOR"],
                "evidence": ["test routing passage"]
              }
            ],
            "obstacles": [
              {
                "id": "routing-obstacle:aggregate-1",
                "pageNumber": 1,
                "sourceId": "aggregate-1",
                "sourceKind": "ObjectAggregate",
                "obstacleKind": "SoftObstacle",
                "routingInfluence": "SoftObstacle",
                "structuralInfluence": "None",
                "category": "Furniture",
                "objectKind": "GenericSymbol",
                "bounds": { "x": 80, "y": 60, "width": 15, "height": 10 },
                "boundsMillimeters": { "x": 800, "y": 600, "width": 150, "height": 100 },
                "center": { "x": 87.5, "y": 65 },
                "centerMillimeters": { "x": 875, "y": 650 },
                "millimetersPerDrawingUnit": 10,
                "label": "Test aggregate",
                "roomId": "room-1",
                "roomLabel": "Test room",
                "suppressesChildObjects": true,
                "childObjectIds": ["object-1", "object-2"],
                "confidence": 0.8,
                "sourcePrimitiveIds": ["object-primitive-1"],
                "sourceLayers": ["A-FURN"],
                "evidence": ["test routing obstacle"]
              }
            ],
            "roomUseHints": [
              {
                "id": "routing-room-use:room-1",
                "pageNumber": 1,
                "sourceId": "room-1",
                "sourceKind": "Room",
                "roomUseKind": "Unknown",
                "bounds": { "x": 20, "y": 40, "width": 100, "height": 50 },
                "boundsMillimeters": { "x": 200, "y": 400, "width": 1000, "height": 500 },
                "center": { "x": 70, "y": 65 },
                "centerMillimeters": { "x": 700, "y": 650 },
                "millimetersPerDrawingUnit": 10,
                "roomId": "room-1",
                "roomLabel": "Test room",
                "confidence": 0.9,
                "sourcePrimitiveIds": [],
                "sourceLayers": [],
                "evidence": ["test routing room-use hint"]
              }
            ],
            "suppressedObjects": [
              {
                "id": "routing-suppression:object-1",
                "pageNumber": 1,
                "objectCandidateId": "object-1",
                "suppressedByAggregateId": "{{suppressedByAggregateId}}",
                "reason": "ReplacedByObjectAggregate",
                "action": "UseAggregateObstacle",
                "replacementRoutingObstacleId": "{{replacementRoutingObstacleId}}",
                "roomUseHintId": null,
                "aggregateRoutingInfluence": "SoftObstacle",
                "aggregateStructuralInfluence": "None",
                "candidateCategory": "Furniture",
                "candidateKind": "GenericSymbol",
                "candidateBounds": { "x": 82, "y": 62, "width": 5, "height": 5 },
                "candidateBoundsMillimeters": { "x": 820, "y": 620, "width": 50, "height": 50 },
                "candidateCenter": { "x": 84.5, "y": 64.5 },
                "candidateCenterMillimeters": { "x": 845, "y": 645 },
                "millimetersPerDrawingUnit": 10,
                "candidateLabel": "Test child object",
                "roomId": "room-1",
                "roomLabel": "Test room",
                "confidence": 0.75,
                "sourcePrimitiveIds": ["object-primitive-1"],
                "sourceLayers": ["A-FURN"],
                "evidence": ["test routing suppression"]
              }
            ],
            "ignoredObjects": [
              {
                "id": "routing-ignored:object-2",
                "pageNumber": 1,
                "objectCandidateId": "object-2",
                "reason": "UnclassifiedReviewCandidate",
                "routingInfluence": "Unknown",
                "structuralInfluence": "NonStructural",
                "candidateCategory": "GenericSymbol",
                "candidateKind": "GenericSymbol",
                "candidateSourceKind": "CadSymbol",
                "sourceWallComponentId": null,
                "sourceWallComponentKind": null,
                "candidateBounds": { "x": 90, "y": 62, "width": 5, "height": 5 },
                "candidateBoundsMillimeters": { "x": 900, "y": 620, "width": 50, "height": 50 },
                "candidateCenter": { "x": 92.5, "y": 64.5 },
                "candidateCenterMillimeters": { "x": 925, "y": 645 },
                "millimetersPerDrawingUnit": 10,
                "candidateLabel": "Review child object",
                "roomId": "room-1",
                "roomLabel": "Test room",
                "suppressedObjectId": null,
                "suppressedByAggregateId": null,
                "roomUseHintId": null,
                "confidence": 0.68,
                "sourcePrimitiveIds": ["object-primitive-1"],
                "sourceLayers": ["A-FURN"],
                "evidence": ["test routing ignored object"]
              }
            ],
            "suppressedObjectCandidateIds": ["object-1"],
            "ignoredObjectCandidateIds": ["object-2"],
            "evidence": ["test routing layer"]
          },
          "issues": {{SurfaceOverlapIssuesJson(includeSurfaceOverlapIssue, surfaceOverlapWallId)}}
        }
        """;

    private const string SurfaceOverlapPatternId = "surface-pattern-1";

    private static string JsonBool(bool value) => value ? "true" : "false";

    private static string SurfaceOverlapWallReasonsJson(bool markSurfaceOverlapWallReview) =>
        markSurfaceOverlapWallReview
            ? JsonSerializer.Serialize(new[]
            {
                $"wall overlaps non-structural surface/detail pattern {SurfaceOverlapPatternId} at wall overlap ratio 0.8"
            })
            : "[]";

    private static string SurfaceOverlapReviewIssueCodesJson(bool includeSurfaceOverlapIssue) =>
        includeSurfaceOverlapIssue
            ? JsonSerializer.Serialize(new[] { "placement.wall_graph.surface_pattern_wall_overlaps.require_review" })
            : "[]";

    private static string SurfaceOverlapRecommendedActionsJson(bool includeSurfaceOverlapIssue) =>
        includeSurfaceOverlapIssue
            ? JsonSerializer.Serialize(new[] { "Review walls that overlap dense surface/detail patterns before using structural topology or room generation." })
            : JsonSerializer.Serialize(new[] { "Placement packet is ready for downstream import; keep benchmark coverage for this source family." });

    private static string SurfaceOverlapIssuesJson(bool includeSurfaceOverlapIssue, string surfaceOverlapWallId) =>
        includeSurfaceOverlapIssue
            ? $$"""
              [
                {
                  "code": "placement.review.surface_pattern_wall_overlap",
                  "severity": "Warning",
                  "message": "A wall overlaps a dense non-structural surface/detail pattern.",
                  "pageNumber": 1,
                  "pageNumbers": [1],
                  "itemId": "review:surface-pattern-wall-overlap:1:1",
                  "bounds": { "x": 20, "y": 38, "width": 100, "height": 4 },
                  "boundsMillimeters": { "x": 200, "y": 380, "width": 1000, "height": 40 },
                  "confidence": 0.8,
                  "recommendedAction": "Review this wall/surface-pattern overlap before using the wall as structural topology.",
                  "sourcePrimitiveIds": ["wall-primitive-1"],
                  "sourceLayers": ["A-WALL"],
                  "evidence": [
                    "A wall overlaps a dense non-structural surface/detail pattern.",
                    "wall id: {{surfaceOverlapWallId}}",
                    "surface pattern id: {{SurfaceOverlapPatternId}}",
                    "wall overlap ratio 0.8"
                  ],
                  "properties": {
                    "wallId": {{JsonSerializer.Serialize(surfaceOverlapWallId)}},
                    "surfacePatternId": {{JsonSerializer.Serialize(SurfaceOverlapPatternId)}},
                    "wallOverlapRatio": "0.8",
                    "diagnosticCode": "wall_graph.surface_pattern_wall_overlap.review",
                    "diagnosticScope": "Detection",
                    "detector": "wall-graph"
                  }
                }
              ]
              """
            : "[]";

    private static async Task<string> CreateGeneratedScanJsonAsync()
    {
        var document = new PlanDocument(
            "cli-scan-validation-plan",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(300, 100))) { SourceId = "wall-top" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(300, 100), new PlanPoint(300, 260))) { SourceId = "wall-right" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(300, 260), new PlanPoint(100, 260))) { SourceId = "wall-bottom" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 260), new PlanPoint(100, 100))) { SourceId = "wall-left" },
                        new TextPrimitive("GARASJE 24 m2", new PlanRect(150, 165, 90, 18)) { SourceId = "room-label" },
                        new TextPrimitive("Malestokk 1:100", new PlanRect(350, 330, 100, 16)) { SourceId = "scale-text" }
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        return PlanTraceJsonExporter.Serialize(
            result,
            new PlanTraceJsonExportOptions { WriteIndented = false });
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _directory;

        private TestWorkspace(string directory)
        {
            _directory = directory;
            Directory.CreateDirectory(_directory);
        }

        public static TestWorkspace Create() =>
            new(Path.Combine(Path.GetTempPath(), "OpenPlanTraceTests", Guid.NewGuid().ToString("N")));

        public string PathFor(string fileName) =>
            Path.Combine(_directory, fileName);

        public string Write(string fileName, string text)
        {
            var path = PathFor(fileName);
            File.WriteAllText(path, text);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
