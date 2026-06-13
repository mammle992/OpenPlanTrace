using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class BenchmarkManifestDraftBuilderTests
{
    [Fact]
    public void FromScanJson_DraftsCountsQualityAndDetectorTargets()
    {
        using var document = JsonDocument.Parse("""
            {
              "schemaVersion": "openplantrace.scan.v42",
              "generatedAt": "2026-06-07T12:00:00Z",
              "document": {
                "id": "industrial-plan",
                "sourceName": "industrial-plan.pdf",
                "sourcePath": "C:\\plans\\industrial-plan.pdf"
              },
              "pages": [{ "number": 1, "width": 1000, "height": 800, "primitiveCount": 50 }],
              "regions": [
                {
                  "id": "region-main",
                  "pageNumber": 1,
                  "kind": "MainFloorPlan",
                  "bounds": { "x": 10, "y": 20, "width": 700, "height": 500 },
                  "confidence": 0.91,
                  "label": "Main",
                  "sourcePrimitiveIds": ["region-src-1"],
                  "sourceLayers": ["A-FLOR"],
                  "evidence": ["largest dense drawing region"]
                }
              ],
              "dimensions": [
                {
                  "id": "dim-width",
                  "pageNumber": 1,
                  "kind": "Linear",
                  "orientation": "Horizontal",
                  "text": "4000 mm",
                  "normalizedText": "4000 mm",
                  "bounds": { "x": 100, "y": 710, "width": 180, "height": 20 },
                  "confidence": 0.88,
                  "sourcePrimitiveIds": ["dim-text-1", "dim-line-1"],
                  "sourceLayers": ["A-DIMS"],
                  "evidence": ["dimension text matched nearby line"]
                }
              ],
              "annotations": [
                {
                  "id": "anno-keynotes",
                  "pageNumber": 1,
                  "kind": "Keynotes",
                  "label": "KEYNOTES",
                  "bounds": { "x": 760, "y": 40, "width": 180, "height": 200 },
                  "confidence": 0.84,
                  "items": [
                    {
                      "id": "anno-item-1",
                      "pageNumber": 1,
                      "kind": "Item",
                      "text": "1 Pump plinth",
                      "marker": "1",
                      "bounds": { "x": 770, "y": 80, "width": 130, "height": 16 },
                      "confidence": 0.8,
                      "references": [
                        {
                          "id": "anno-ref-1",
                          "marker": "1",
                          "text": "1",
                          "bounds": { "x": 250, "y": 250, "width": 18, "height": 18 },
                          "confidence": 0.79,
                          "sourcePrimitiveIds": ["keynote-ref-text-1"],
                          "sourceLayers": ["A-ANNO"],
                          "evidence": ["matched keynote marker 1"]
                        }
                      ]
                    }
                  ]
                }
              ],
              "gridAxes": [
                {
                  "id": "grid-a",
                  "pageNumber": 1,
                  "orientation": "Vertical",
                  "label": "A",
                  "bounds": { "x": 200, "y": 30, "width": 4, "height": 520 },
                  "confidence": 0.86
                }
              ],
              "gridBaySpacings": [{ "id": "bay-a-b" }],
              "surfacePatterns": [{ "id": "surface-pattern-1" }],
              "walls": [
                {
                  "id": "wall-1",
                  "pageNumber": 1,
                  "bounds": { "x": 100, "y": 100, "width": 300, "height": 12 },
                  "confidence": 0.77
                }
              ],
              "wallGraph": {
                "nodes": [{ "id": "n1" }, { "id": "n2" }],
                "edges": [{ "id": "e1" }]
              },
              "rooms": [
                {
                  "id": "room-1",
                  "pageNumber": 1,
                  "bounds": { "x": 120, "y": 120, "width": 260, "height": 200 },
                  "label": "PUMP ROOM",
                  "confidence": 0.82,
                  "labelSourcePrimitiveIds": ["room-label-1"],
                  "evidence": ["room label is inside closed boundary"]
                }
              ],
              "roomAdjacencyGraph": {
                "edges": [{ "id": "adj-1" }],
                "clusters": [{ "id": "cluster-1" }]
              },
              "openings": [
                {
                  "id": "door-1",
                  "pageNumber": 1,
                  "type": "Door",
                  "operation": "Hinged",
                  "bounds": { "x": 180, "y": 100, "width": 42, "height": 20 },
                  "confidence": 0.74
                }
              ],
              "objects": [
                {
                  "id": "object-1",
                  "pageNumber": 1,
                  "kind": "Symbol",
                  "category": "HVACEquipment",
                  "symbolName": "AHU-1",
                  "detectedTag": "AHU-1",
                  "detectedTagSourcePrimitiveId": "ahu-tag-1",
                  "bounds": { "x": 420, "y": 180, "width": 60, "height": 45 },
                  "confidence": 0.78
                }
              ],
              "objectGroups": [
                {
                  "id": "group-ahu",
                  "signature": "AHU",
                  "kind": "Symbol",
                  "category": "HVACEquipment",
                  "count": 3,
                  "representativeBounds": { "x": 420, "y": 180, "width": 60, "height": 45 },
                  "pageNumbers": [1],
                  "requiresReview": true,
                  "symbolName": "AHU-1",
                  "detectedTags": ["AHU-1", "AHU-2", "AHU-3"],
                  "confidence": 0.76,
                  "sourcePrimitiveIds": ["ahu-block-1", "ahu-block-2", "ahu-block-3"],
                  "sourceLayers": ["M-HVAC-EQPM"],
                  "evidence": ["three matching symbol signatures"]
                }
              ],
              "objectAggregates": [
                {
                  "id": "aggregate-car-1",
                  "pageNumber": 1,
                  "category": "Vehicle",
                  "kind": "Vehicle",
                  "childObjectCount": 23,
                  "childObjectIds": ["car-body", "car-wheel-1", "car-wheel-2"],
                  "objectGroupIds": ["group-car"],
                  "sourcePrimitiveIds": ["car-body", "car-wheel-1", "car-wheel-2"],
                  "routingInfluence": "RoomUseEvidenceOnly",
                  "structuralInfluence": "None",
                  "suppressChildObjectsForRouting": true,
                  "roomUseEvidence": "Parking",
                  "bounds": { "x": 210, "y": 420, "width": 150, "height": 70 },
                  "label": "car",
                  "roomId": "room-garage",
                  "roomLabel": "GARAGE",
                  "requiresReview": false,
                  "confidence": 0.83,
                  "sourceLayers": ["A-VEHICLE-CAR"],
                  "evidence": ["vehicle aggregate contributes semantic room-use evidence"]
                }
              ],
              "routingLayer": {
                "barriers": [
                  {
                    "id": "routing-barrier-wall-1",
                    "pageNumber": 1,
                    "sourceId": "wall-1",
                    "sourceKind": "Wall",
                    "bounds": { "x": 100, "y": 100, "width": 300, "height": 12 },
                    "confidence": 0.77,
                    "sourcePrimitiveIds": ["wall-1"],
                    "sourceLayers": ["A-WALL"],
                    "evidence": ["wall exported as routing barrier"]
                  }
                ],
                "passages": [
                  {
                    "id": "routing-passage-door-1",
                    "pageNumber": 1,
                    "sourceId": "door-1",
                    "sourceKind": "Opening",
                    "type": "Door",
                    "operation": "Hinged",
                    "bounds": { "x": 180, "y": 100, "width": 42, "height": 20 },
                    "confidence": 0.74,
                    "sourcePrimitiveIds": ["door-1"],
                    "sourceLayers": ["A-DOOR"],
                    "evidence": ["door exported as routing passage"]
                  }
                ],
                "obstacles": [
                  {
                    "id": "routing-obstacle-ahu-1",
                    "pageNumber": 1,
                    "sourceId": "object-1",
                    "sourceKind": "ObjectCandidate",
                    "obstacleKind": "HardObstacle",
                    "routingInfluence": "HardObstacle",
                    "structuralInfluence": "FixedEquipment",
                    "category": "HVACEquipment",
                    "objectKind": "Symbol",
                    "bounds": { "x": 420, "y": 180, "width": 60, "height": 45 },
                    "label": "AHU-1",
                    "roomId": "room-1",
                    "roomLabel": "PUMP ROOM",
                    "suppressesChildObjects": false,
                    "childObjectIds": [],
                    "confidence": 0.78,
                    "sourcePrimitiveIds": ["ahu-tag-1"],
                    "sourceLayers": ["M-HVAC-EQPM"],
                    "evidence": ["HVAC equipment is a hard routing obstacle"]
                  }
                ],
                "roomUseHints": [
                  {
                    "id": "routing-room-use-aggregate-car-1",
                    "pageNumber": 1,
                    "sourceId": "aggregate-car-1",
                    "sourceKind": "ObjectAggregate",
                    "roomUseKind": "Parking",
                    "bounds": { "x": 210, "y": 420, "width": 150, "height": 70 },
                    "roomId": "room-garage",
                    "roomLabel": "GARAGE",
                    "confidence": 0.83,
                    "sourcePrimitiveIds": ["car-body", "car-wheel-1", "car-wheel-2"],
                    "sourceLayers": ["A-VEHICLE-CAR"],
                    "evidence": ["vehicle aggregate implies parking use"]
                  }
                ],
                "suppressedObjects": [
                  {
                    "id": "routing-suppression-car-body",
                    "pageNumber": 1,
                    "objectCandidateId": "car-body",
                    "suppressedByAggregateId": "aggregate-car-1",
                    "reason": "AggregateRoomUseEvidenceOnly",
                    "action": "UseAggregateRoomUseHint",
                    "replacementRoutingObstacleId": null,
                    "roomUseHintId": "routing-room-use-aggregate-car-1",
                    "aggregateRoutingInfluence": "RoomUseEvidenceOnly",
                    "aggregateStructuralInfluence": "None",
                    "candidateCategory": "Vehicle",
                    "candidateKind": "Vehicle",
                    "candidateBounds": { "x": 210, "y": 420, "width": 150, "height": 70 },
                    "candidateLabel": "car",
                    "roomId": "room-garage",
                    "roomLabel": "GARAGE",
                    "confidence": 0.81,
                    "sourcePrimitiveIds": ["car-body"],
                    "sourceLayers": ["A-VEHICLE-CAR"],
                    "evidence": ["car-body is represented by aggregate-car-1 for routing"]
                  }
                ],
                "ignoredObjects": [],
                "suppressedObjectCandidateIds": ["car-body", "car-wheel-1", "car-wheel-2"],
                "ignoredObjectCandidateIds": [],
                "evidence": ["routing layer generated from walls, openings, and objects"]
              },
              "layerAnalysis": {
                "layers": [
                  {
                    "name": "M-HVAC-EQPM",
                    "likelyCategory": "HVAC",
                    "bounds": { "x": 410, "y": 170, "width": 90, "height": 70 },
                    "confidence": 0.81,
                    "evidence": ["layer name contains HVAC equipment hint"]
                  }
                ]
              },
              "quality": {
                "overallConfidence": 0.92,
                "grade": "Strong",
                "hasReliableCalibration": true,
                "diagnosticWarningCount": 1,
                "diagnosticErrorCount": 0,
                "issues": [
                  { "code": "quality.object_groups_require_review" },
                  { "code": "quality.scan_risk.sheet_contamination" }
                ]
              },
              "reviewQueue": [
                {
                  "id": "review:object-group:group-ahu",
                  "kind": "ObjectGroupReview"
                },
                {
                  "id": "review:wall-graph-gap:1:1",
                  "kind": "WallGraphGapReview"
                }
              ],
              "importReadiness": {
                "grade": "Strong",
                "score": 0.94,
                "readyForGeometryImport": true,
                "readyForMetricImport": true,
                "readyForRoutingImport": true,
                "requiresReview": false
              },
              "measurementConsistency": {
                "hasReliableCalibration": true,
                "checkedCount": 5,
                "consistentCount": 3,
                "outlierCount": 2,
                "dimensionScaleSpreadRatio": 2.25
              },
              "diagnostics": {
                "warningCount": 1,
                "errorCount": 0
              }
            }
            """);

        var manifest = BenchmarkManifestDraftBuilder.FromScanJson(
            document,
            new BenchmarkManifestDraftOptions
            {
                FixtureId = "industrial-plan",
                ManifestName = "Industrial plan draft",
                SourcePath = "%USERPROFILE%\\Downloads\\industrial-plan.pdf"
            });

        Assert.Equal(BenchmarkManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Equal("Industrial plan draft", manifest.Name);
        var fixture = Assert.Single(manifest.Fixtures);
        Assert.Equal("industrial-plan", fixture.Id);
        Assert.Equal("industrial-plan.pdf", fixture.Name);
        Assert.Equal("%USERPROFILE%\\Downloads\\industrial-plan.pdf", fixture.SourcePath);
        Assert.Equal("scan-json", fixture.Properties["draftedFrom"]);
        Assert.Equal("openplantrace.scan.v42", fixture.Properties["scanSchemaVersion"]);

        var expectations = fixture.Expectations;
        Assert.Equal(1, expectations.MinPages);
        Assert.Equal(1, expectations.MinRegions);
        Assert.Equal(1, expectations.MinDimensions);
        Assert.Equal(1, expectations.MinAnnotations);
        Assert.Equal(1, expectations.MinAnnotationReferences);
        Assert.Equal(1, expectations.MinGridAxes);
        Assert.Equal(1, expectations.MinGridBaySpacings);
        Assert.Equal(1, expectations.MinSurfacePatterns);
        Assert.Equal(1, expectations.MinWalls);
        Assert.Equal(2, expectations.MinWallNodes);
        Assert.Equal(1, expectations.MinWallEdges);
        Assert.Equal(1, expectations.MinRooms);
        Assert.Equal(1, expectations.MinRoomAdjacencies);
        Assert.Equal(1, expectations.MinRoomClusters);
        Assert.Equal(1, expectations.MinOpenings);
        Assert.Equal(1, expectations.MinObjects);
        Assert.Equal(1, expectations.MinObjectGroups);
        Assert.Equal(1, expectations.MinObjectAggregates);
        Assert.Equal(4, expectations.MinRoutingItems);
        Assert.Equal(1, expectations.MinRoutingSuppressedObjects);
        Assert.Equal(1, expectations.MaxDiagnosticWarnings);
        Assert.Equal(0, expectations.MaxDiagnosticErrors);
        Assert.Equal(PlanScanQualityGrade.Usable, expectations.MinQualityGrade);
        Assert.Equal(0.82, expectations.MinQualityConfidence);
        Assert.Equal(2, expectations.MaxQualityIssues);
        Assert.Equal(1, expectations.MaxScanRiskIssues);
        Assert.Equal(2, expectations.MaxScanReviewQueueItems);
        Assert.Equal(1, expectations.MaxScanReviewQueueKindCounts["ObjectGroupReview"]);
        Assert.Equal(1, expectations.MaxScanReviewQueueKindCounts["WallGraphGapReview"]);
        Assert.Equal(PlanImportReadinessGrade.Usable, expectations.MinImportReadinessGrade);
        Assert.Equal(0.84, expectations.MinImportReadinessScore);
        Assert.True(expectations.RequireGeometryImportReady);
        Assert.True(expectations.RequireMetricImportReady);
        Assert.True(expectations.RequireRoutingImportReady);
        Assert.False(expectations.AllowImportReview);
        Assert.True(expectations.RequiresReliableCalibration);
        Assert.Equal(5, expectations.MinMeasurementCheckedCount);
        Assert.Equal(3, expectations.MinMeasurementConsistentCount);
        Assert.Equal(2, expectations.MaxMeasurementOutlierCount);
        Assert.Equal(0.4, expectations.MaxMeasurementOutlierRatio);
        Assert.Equal(2.25, expectations.MaxMeasurementScaleSpreadRatio);

        var regionTarget = Assert.Single(expectations.RegionMetrics.Targets);
        Assert.Equal(RegionKind.MainFloorPlan, regionTarget.RegionKind);
        Assert.Equal(new PlanRect(10, 20, 700, 500), regionTarget.Bounds);
        Assert.Equal(0.91, regionTarget.Confidence);
        Assert.Contains("region-src-1", regionTarget.SourcePrimitiveIds!);
        Assert.Contains("A-FLOR", regionTarget.SourceLayers!);
        Assert.Contains("largest dense drawing region", regionTarget.Evidence!);
        var dimensionTarget = Assert.Single(expectations.DimensionMetrics.Targets);
        Assert.Equal("4000 mm", dimensionTarget.Text);
        Assert.Equal(DimensionOrientation.Horizontal, dimensionTarget.DimensionOrientation);
        Assert.Contains("dim-line-1", dimensionTarget.SourcePrimitiveIds!);
        var annotationReferenceTarget = Assert.Single(expectations.AnnotationReferenceMetrics.Targets);
        Assert.Equal("1", annotationReferenceTarget.Marker);
        Assert.Equal(PlanAnnotationKind.Keynotes, annotationReferenceTarget.AnnotationKind);
        Assert.Equal(0.79, annotationReferenceTarget.Confidence);
        Assert.Contains("keynote-ref-text-1", annotationReferenceTarget.SourcePrimitiveIds!);
        var openingTarget = Assert.Single(expectations.OpeningMetrics.Targets);
        Assert.Equal(OpeningType.Door, openingTarget.OpeningType);
        Assert.Equal(OpeningOperation.Hinged, openingTarget.OpeningOperation);
        var roomTarget = Assert.Single(expectations.RoomMetrics.Targets);
        Assert.Contains("room-label-1", roomTarget.SourcePrimitiveIds!);
        var objectTarget = Assert.Single(expectations.ObjectMetrics.Targets);
        Assert.Equal(ObjectCandidateKind.Symbol, objectTarget.ObjectKind);
        Assert.Equal(new[] { "AHU-1" }, objectTarget.DetectedTags);
        var objectGroupTarget = Assert.Single(expectations.ObjectGroupMetrics.Targets);
        Assert.Equal(3, objectGroupTarget.MinCount);
        Assert.True(objectGroupTarget.RequiresReview);
        Assert.Equal(ObjectCategory.HVACEquipment, objectGroupTarget.ObjectCategory);
        Assert.Equal(ObjectCandidateKind.Symbol, objectGroupTarget.ObjectKind);
        Assert.Equal(new[] { "AHU-1", "AHU-2", "AHU-3" }, objectGroupTarget.DetectedTags);
        Assert.Contains("ahu-block-3", objectGroupTarget.SourcePrimitiveIds!);
        Assert.Contains("three matching symbol signatures", objectGroupTarget.Evidence!);
        var aggregateTarget = Assert.Single(expectations.ObjectAggregateMetrics.Targets);
        Assert.Equal(ObjectCategory.Vehicle, aggregateTarget.ObjectCategory);
        Assert.Equal(ObjectCandidateKind.Vehicle, aggregateTarget.ObjectKind);
        Assert.Equal(23, aggregateTarget.MinCount);
        Assert.Equal(ObjectRoutingInfluence.RoomUseEvidenceOnly, aggregateTarget.RoutingInfluence);
        Assert.Equal(ObjectStructuralInfluence.None, aggregateTarget.StructuralInfluence);
        Assert.True(aggregateTarget.SuppressesChildObjects.GetValueOrDefault());
        Assert.Equal(RoomUseKind.Parking, aggregateTarget.RoomUseKind);
        Assert.Contains("A-VEHICLE-CAR", aggregateTarget.SourceLayers!);
        var routingBarrierTarget = Assert.Single(expectations.RoutingBarrierMetrics.Targets);
        Assert.Equal(RoutingSourceKind.Wall, routingBarrierTarget.RoutingSourceKind);
        var routingPassageTarget = Assert.Single(expectations.RoutingPassageMetrics.Targets);
        Assert.Equal(OpeningType.Door, routingPassageTarget.OpeningType);
        Assert.Equal(OpeningOperation.Hinged, routingPassageTarget.OpeningOperation);
        Assert.Equal(RoutingSourceKind.Opening, routingPassageTarget.RoutingSourceKind);
        var routingObstacleTarget = Assert.Single(expectations.RoutingObstacleMetrics.Targets);
        Assert.Equal(RoutingObstacleKind.HardObstacle, routingObstacleTarget.RoutingObstacleKind);
        Assert.Equal(ObjectRoutingInfluence.HardObstacle, routingObstacleTarget.RoutingInfluence);
        Assert.Equal(ObjectStructuralInfluence.FixedEquipment, routingObstacleTarget.StructuralInfluence);
        Assert.Equal(ObjectCategory.HVACEquipment, routingObstacleTarget.ObjectCategory);
        Assert.False(routingObstacleTarget.SuppressesChildObjects.GetValueOrDefault());
        var routingRoomUseHintTarget = Assert.Single(expectations.RoutingRoomUseHintMetrics.Targets);
        Assert.Equal(RoutingSourceKind.ObjectAggregate, routingRoomUseHintTarget.RoutingSourceKind);
        Assert.Equal(RoomUseKind.Parking, routingRoomUseHintTarget.RoomUseKind);
        Assert.Equal("GARAGE", routingRoomUseHintTarget.Label);
        var routingSuppressedObjectTarget = Assert.Single(expectations.RoutingSuppressedObjectMetrics.Targets);
        Assert.Equal("car-body", routingSuppressedObjectTarget.ObjectCandidateId);
        Assert.Equal("aggregate-car-1", routingSuppressedObjectTarget.SuppressedByAggregateId);
        Assert.Equal(RoutingSuppressionReason.AggregateRoomUseEvidenceOnly, routingSuppressedObjectTarget.SuppressionReason);
        Assert.Equal(RoutingSuppressedObjectAction.UseAggregateRoomUseHint, routingSuppressedObjectTarget.SuppressionAction);
        Assert.Equal("routing-room-use-aggregate-car-1", routingSuppressedObjectTarget.RoomUseHintId);
        Assert.Equal(ObjectCategory.Vehicle, routingSuppressedObjectTarget.ObjectCategory);
        Assert.Equal(ObjectCandidateKind.Vehicle, routingSuppressedObjectTarget.ObjectKind);
        Assert.Equal(ObjectRoutingInfluence.RoomUseEvidenceOnly, routingSuppressedObjectTarget.RoutingInfluence);
        Assert.Equal(ObjectStructuralInfluence.None, routingSuppressedObjectTarget.StructuralInfluence);
        Assert.Contains("car-body", routingSuppressedObjectTarget.SourcePrimitiveIds!);
        var layerTarget = Assert.Single(expectations.LayerMetrics.Targets);
        Assert.Equal("M-HVAC-EQPM", layerTarget.Label);
        Assert.Equal(LayerCategory.HVAC, layerTarget.LayerCategory);
        Assert.Equal(0.81, layerTarget.Confidence);
        Assert.Contains("layer name contains HVAC equipment hint", layerTarget.Evidence!);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var manifestJson = JsonSerializer.Serialize(manifest, jsonOptions);
        Assert.Contains("\"schemaVersion\":\"openplantrace.benchmark-manifest.v1\"", manifestJson);
        Assert.Contains("\"regionKind\":\"MainFloorPlan\"", manifestJson);
        Assert.Contains("\"detectedTags\":[\"AHU-1\"]", manifestJson);
        Assert.Contains("\"routingInfluence\":\"RoomUseEvidenceOnly\"", manifestJson);
        Assert.Contains("\"routingSuppressedObjectMetrics\"", manifestJson);
        Assert.Contains("\"suppressionAction\":\"UseAggregateRoomUseHint\"", manifestJson);
        Assert.Contains("\"routingObstacleKind\":\"HardObstacle\"", manifestJson);
        Assert.Contains("\"roomUseKind\":\"Parking\"", manifestJson);
        Assert.Contains("\"sourcePrimitiveIds\":[\"region-src-1\"]", manifestJson);
    }

    [Fact]
    public void FromScanJson_AppliesOptionalTargetCapAndMetricThresholds()
    {
        using var document = JsonDocument.Parse("""
            {
              "document": { "sourceName": "two-room.pdf" },
              "rooms": [
                {
                  "id": "room-high",
                  "pageNumber": 1,
                  "label": "HIGH",
                  "bounds": { "x": 10, "y": 10, "width": 100, "height": 80 },
                  "confidence": 0.9
                },
                {
                  "id": "room-low",
                  "pageNumber": 1,
                  "label": "LOW",
                  "bounds": { "x": 120, "y": 10, "width": 100, "height": 80 },
                  "confidence": 0.5
                }
              ]
            }
            """);

        var manifest = BenchmarkManifestDraftBuilder.FromScanJson(
            document,
            new BenchmarkManifestDraftOptions
            {
                Optional = true,
                SkipReason = "Local source fixture is optional.",
                MaxTargetsPerDetector = 1,
                TargetRecall = 0.75,
                TargetPrecision = 0.5,
                IncludeBounds = false
            });

        var fixture = Assert.Single(manifest.Fixtures);
        Assert.True(fixture.Optional);
        Assert.Equal("Local source fixture is optional.", fixture.SkipReason);
        Assert.Equal("two-room.pdf", fixture.SourcePath);
        Assert.Equal(2, fixture.Expectations.MinRooms);
        Assert.Equal(0.75, fixture.Expectations.RoomMetrics.MinRecall);
        Assert.Equal(0.5, fixture.Expectations.RoomMetrics.MinPrecision);
        var target = Assert.Single(fixture.Expectations.RoomMetrics.Targets);
        Assert.Equal("HIGH", target.Label);
        Assert.Null(target.Bounds);
    }

    [Fact]
    public void FromScanJson_RejectsInvalidDraftOptions()
    {
        using var document = JsonDocument.Parse("""{ "rooms": [] }""");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BenchmarkManifestDraftBuilder.FromScanJson(
                document,
                new BenchmarkManifestDraftOptions { TargetRecall = 1.2 }));
    }
}
