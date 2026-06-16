using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class OpeningSemanticsTests
{
    [Fact]
    public async Task ScanAsync_AddsHingedDoorSwingSemantics()
    {
        var document = new PlanDocument(
            "hinged-door",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-left-run", new PlanPoint(100, 100), new PlanPoint(220, 100)),
                        Wall("wall-right-run", new PlanPoint(250, 100), new PlanPoint(400, 100)),
                        new ArcPrimitive(new PlanPoint(220, 100), 30, 0, Math.PI / 2) { SourceId = "door-swing" }
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var opening = Assert.Single(result.Openings);

        Assert.Equal(OpeningType.Door, opening.Type);
        Assert.Equal(OpeningOperation.Hinged, opening.Operation);
        Assert.Equal(OpeningOrientation.Horizontal, opening.Orientation);
        Assert.Equal(OpeningHingeSide.StartJamb, opening.HingeSide);
        Assert.Equal(OpeningSwingSide.PositiveCoordinateSide, opening.SwingSide);
        Assert.Equal(OpeningSwingDirection.CounterClockwise, opening.SwingDirection);
        Assert.Equal(new PlanPoint(220, 100), opening.HingePoint);
        Assert.Equal(30, opening.DrawingWidth);
        Assert.Equal(2, opening.HostWallIds.Count);
        Assert.NotNull(opening.Placement);
        Assert.Equal(2, opening.Placement.AnchorWallIds.Count);
        Assert.Equal(120, opening.Placement.StartOffsetDrawingUnits, 3);
        Assert.Equal(150, opening.Placement.EndOffsetDrawingUnits, 3);
        Assert.Equal(135, opening.Placement.CenterOffsetDrawingUnits, 3);
        Assert.Equal(30, opening.Placement.LengthDrawingUnits, 3);
        Assert.Equal(4, opening.Placement.DepthDrawingUnits, 3);
        Assert.Equal(new PlanRect(220, 98, 30, 4), opening.Placement.FootprintBounds);
        Assert.Equal(4, opening.Placement.FootprintCorners.Count);
        Assert.Equal(new PlanPoint(220, 98), opening.Placement.FootprintCorners[0]);
        Assert.Equal(new PlanPoint(250, 98), opening.Placement.FootprintCorners[1]);
        Assert.Equal(new PlanPoint(250, 102), opening.Placement.FootprintCorners[2]);
        Assert.Equal(new PlanPoint(220, 102), opening.Placement.FootprintCorners[3]);
        Assert.Equal(new PlanLineSegment(new PlanPoint(220, 98), new PlanPoint(220, 102)), opening.Placement.StartJambLine);
        Assert.Equal(new PlanLineSegment(new PlanPoint(250, 98), new PlanPoint(250, 102)), opening.Placement.EndJambLine);
        Assert.Equal(new PlanPoint(100, 100), opening.Placement.ReferenceLine.Start);
        Assert.Equal(new PlanPoint(400, 100), opening.Placement.ReferenceLine.End);
        Assert.Equal(1, opening.Placement.AlongVector.X, 3);
        Assert.Equal(0, opening.Placement.AlongVector.Y, 3);
        Assert.Equal(0, opening.Placement.NormalVector.X, 3);
        Assert.Equal(1, opening.Placement.NormalVector.Y, 3);
        Assert.Contains(opening.Placement.Evidence, item => item.Contains("projected", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(opening.Placement.Evidence, item => item.Contains("footprint", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(opening.Evidence, item => item.Contains("door swing arc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_ClassifiesSlidingDoorFromDoorLayerTrackLine()
    {
        var document = new PlanDocument(
            "sliding-door",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-left-run", new PlanPoint(100, 100), new PlanPoint(220, 100)),
                        Wall("wall-right-run", new PlanPoint(250, 100), new PlanPoint(400, 100)),
                        ShortLine("sliding-track", "A-DOOR-SLIDING", new PlanPoint(225, 104), new PlanPoint(245, 104))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var opening = Assert.Single(result.Openings);

        Assert.Equal(OpeningType.Door, opening.Type);
        Assert.Equal(OpeningOperation.Sliding, opening.Operation);
        Assert.Equal(OpeningOrientation.Horizontal, opening.Orientation);
        Assert.Contains("sliding-track", opening.SourcePrimitiveIds);
        Assert.Contains(opening.Evidence, item => item.Contains("parallel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Walls, wall => wall.SourcePrimitiveIds.Contains("sliding-track"));
    }

    [Fact]
    public async Task ScanAsync_ClassifiesPocketDoorFromPocketLayerTrackLine()
    {
        var document = new PlanDocument(
            "pocket-door",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-left-run", new PlanPoint(100, 100), new PlanPoint(220, 100)),
                        Wall("wall-right-run", new PlanPoint(255, 100), new PlanPoint(400, 100)),
                        ShortLine("pocket-track", "A-DOOR-POCKET", new PlanPoint(225, 104), new PlanPoint(252, 104))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var opening = Assert.Single(result.Openings);

        Assert.Equal(OpeningType.Door, opening.Type);
        Assert.Equal(OpeningOperation.PocketSliding, opening.Operation);
        Assert.Equal(OpeningOrientation.Horizontal, opening.Orientation);
        Assert.Contains("pocket-track", opening.SourcePrimitiveIds);
        Assert.Contains(opening.Evidence, item => item.Contains("pocket", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Walls, wall => wall.SourcePrimitiveIds.Contains("pocket-track"));
    }

    [Fact]
    public async Task ScanAsync_ClassifiesWindowFromWindowLayerLines()
    {
        var document = new PlanDocument(
            "window-opening",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-left-run", new PlanPoint(100, 100), new PlanPoint(220, 100)),
                        Wall("wall-right-run", new PlanPoint(250, 100), new PlanPoint(400, 100)),
                        ShortLine("window-tick-a", "A-WINDOW", new PlanPoint(226, 94), new PlanPoint(226, 106)),
                        ShortLine("window-tick-b", "A-WINDOW", new PlanPoint(244, 94), new PlanPoint(244, 106))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var opening = Assert.Single(result.Openings);

        Assert.Equal(OpeningType.Window, opening.Type);
        Assert.Equal(OpeningOperation.Fixed, opening.Operation);
        Assert.Contains(opening.Evidence, item => item.Contains("window", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_DetectsWindowTicksOnContinuousWall()
    {
        var document = new PlanDocument(
            "continuous-wall-window",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-run", new PlanPoint(100, 100), new PlanPoint(400, 100)),
                        ShortLine("window-tick-a", "A-WINDOW", new PlanPoint(220, 94), new PlanPoint(220, 106)),
                        ShortLine("window-tick-b", "A-WINDOW", new PlanPoint(250, 94), new PlanPoint(250, 106))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var opening = Assert.Single(result.Openings);

        Assert.Equal(OpeningType.Window, opening.Type);
        Assert.Equal(OpeningOperation.Fixed, opening.Operation);
        Assert.Equal(OpeningOrientation.Horizontal, opening.Orientation);
        Assert.Equal(30, opening.DrawingWidth);
        Assert.Equal(new[] { "page:1:wall:1" }, opening.HostWallIds);
        Assert.Contains("window-tick-a", opening.SourcePrimitiveIds);
        Assert.Contains("window-tick-b", opening.SourcePrimitiveIds);
        Assert.Contains(opening.Evidence, item => item.Contains("paired perpendicular", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_DetectsHingedDoorArcOnContinuousWall()
    {
        var document = new PlanDocument(
            "continuous-wall-arc-door",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-run", new PlanPoint(100, 100), new PlanPoint(400, 100)),
                        DoorArc("door-swing", new PlanPoint(220, 100), 30, 0, Math.PI / 2),
                        DoorLeaf("door-leaf", new PlanPoint(220, 100), new PlanPoint(220, 130))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var opening = Assert.Single(result.Openings);

        Assert.Equal(OpeningType.Door, opening.Type);
        Assert.Equal(OpeningOperation.Hinged, opening.Operation);
        Assert.Equal(OpeningOrientation.Horizontal, opening.Orientation);
        Assert.Equal(OpeningHingeSide.StartJamb, opening.HingeSide);
        Assert.Equal(OpeningSwingSide.PositiveCoordinateSide, opening.SwingSide);
        Assert.Equal(OpeningSwingDirection.CounterClockwise, opening.SwingDirection);
        Assert.Equal(new PlanPoint(220, 100), opening.HingePoint);
        Assert.Equal(30, opening.DrawingWidth);
        Assert.Equal(new[] { "page:1:wall:1" }, opening.HostWallIds);
        Assert.Contains("door-swing", opening.SourcePrimitiveIds);
        Assert.Contains("door-leaf", opening.SourcePrimitiveIds);
        Assert.Contains(opening.Evidence, item => item.Contains("door swing arc attached", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics.Messages, message => message.Code == "openings.arc_door_candidates.detected");
    }

    [Fact]
    public async Task ScanAsync_MergesPairedContinuousWallArcsIntoDoubleSwingDoor()
    {
        var document = new PlanDocument(
            "continuous-wall-double-swing-door",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-run", new PlanPoint(100, 100), new PlanPoint(400, 100)),
                        DoorArc("door-swing-left", new PlanPoint(220, 100), 30, 0, Math.PI / 2),
                        DoorArc("door-swing-right", new PlanPoint(280, 100), 30, Math.PI, -Math.PI / 2)
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var opening = Assert.Single(result.Openings);

        Assert.Equal(OpeningType.Door, opening.Type);
        Assert.Equal(OpeningOperation.DoubleSwing, opening.Operation);
        Assert.Equal(OpeningOrientation.Horizontal, opening.Orientation);
        Assert.Equal(OpeningHingeSide.Unknown, opening.HingeSide);
        Assert.Null(opening.HingePoint);
        Assert.Equal(60, opening.DrawingWidth);
        Assert.Equal(new[] { "page:1:wall:1" }, opening.HostWallIds);
        Assert.Contains("door-swing-left", opening.SourcePrimitiveIds);
        Assert.Contains("door-swing-right", opening.SourcePrimitiveIds);
        Assert.Contains(opening.Evidence, item => item.Contains("double-swing", StringComparison.OrdinalIgnoreCase));

        var diagnostic = Assert.Single(result.Diagnostics.Messages, message => message.Code == "openings.arc_door_candidates.detected");
        Assert.Equal("1", diagnostic.Properties["candidateCount"]);
        Assert.Equal("1", diagnostic.Properties["doubleSwingCount"]);

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("DoubleSwing", parsed.RootElement.GetProperty("openings")[0].GetProperty("operation").GetString());
    }

    [Fact]
    public async Task ScanAsync_DoesNotPromoteUnhintedArcOnContinuousWall()
    {
        var document = new PlanDocument(
            "continuous-wall-unhinted-arc",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-run", new PlanPoint(100, 100), new PlanPoint(400, 100)),
                        new ArcPrimitive(new PlanPoint(220, 100), 30, 0, Math.PI / 2) { SourceId = "unhinted-arc" }
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Empty(result.Openings);
        Assert.DoesNotContain(result.Diagnostics.Messages, message => message.Code == "openings.arc_door_candidates.detected");
    }

    [Fact]
    public async Task ScanAsync_DetectsOpeningWhenOverlappingWallFragmentMasksHostGap()
    {
        var document = new PlanDocument(
            "masked-opening-gap",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-left-run", new PlanPoint(100, 100), new PlanPoint(220, 100)),
                        Wall("wall-short-overlap", new PlanPoint(150, 100), new PlanPoint(180, 100)),
                        Wall("wall-right-run", new PlanPoint(250, 100), new PlanPoint(400, 100)),
                        ShortLine("window-tick-a", "A-WINDOW", new PlanPoint(226, 94), new PlanPoint(226, 106)),
                        ShortLine("window-tick-b", "A-WINDOW", new PlanPoint(244, 94), new PlanPoint(244, 106))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var opening = Assert.Single(result.Openings);

        Assert.Equal(OpeningType.Window, opening.Type);
        Assert.Equal(OpeningOperation.Fixed, opening.Operation);
        Assert.Equal(30, opening.DrawingWidth);
        Assert.Equal(2, opening.HostWallIds.Count);
        Assert.Contains(result.Walls, wall => wall.SourcePrimitiveIds.Contains("wall-short-overlap"));
    }

    [Fact]
    public async Task JsonExporter_IncludesOpeningSemanticFields()
    {
        var document = new PlanDocument(
            "opening-export",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-left-run", new PlanPoint(100, 100), new PlanPoint(220, 100)),
                        Wall("wall-right-run", new PlanPoint(250, 100), new PlanPoint(400, 100)),
                        new ArcPrimitive(new PlanPoint(250, 100), 30, Math.PI, -Math.PI / 2) { SourceId = "door-swing" }
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var opening = parsed.RootElement.GetProperty("openings")[0];

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("Door", opening.GetProperty("type").GetString());
        Assert.Equal("Hinged", opening.GetProperty("operation").GetString());
        Assert.Equal("Horizontal", opening.GetProperty("orientation").GetString());
        Assert.Equal("EndJamb", opening.GetProperty("hingeSide").GetString());
        Assert.Equal("Clockwise", opening.GetProperty("swingDirection").GetString());
        Assert.True(opening.GetProperty("hostWallIds").GetArrayLength() == 2);
        Assert.True(opening.GetProperty("evidence").GetArrayLength() > 0);
        Assert.True(opening.GetProperty("centerLine").GetProperty("start").GetProperty("x").GetDouble() > 0);
        var placement = opening.GetProperty("placement");
        Assert.Equal(2, placement.GetProperty("anchorWallIds").GetArrayLength());
        Assert.Equal(120, placement.GetProperty("startOffsetDrawingUnits").GetDouble(), 3);
        Assert.Equal(150, placement.GetProperty("endOffsetDrawingUnits").GetDouble(), 3);
        Assert.Equal(30, placement.GetProperty("lengthDrawingUnits").GetDouble(), 3);
        Assert.Equal(4, placement.GetProperty("depthDrawingUnits").GetDouble(), 3);
        Assert.Equal(4, placement.GetProperty("footprintCorners").GetArrayLength());
        Assert.Equal(220, placement.GetProperty("footprintBounds").GetProperty("x").GetDouble(), 3);
        Assert.Equal(98, placement.GetProperty("footprintBounds").GetProperty("y").GetDouble(), 3);
        Assert.Equal(30, placement.GetProperty("footprintBounds").GetProperty("width").GetDouble(), 3);
        Assert.Equal(4, placement.GetProperty("footprintBounds").GetProperty("height").GetDouble(), 3);
        Assert.Equal(220, placement.GetProperty("startJambLine").GetProperty("start").GetProperty("x").GetDouble(), 3);
        Assert.Equal(102, placement.GetProperty("startJambLine").GetProperty("end").GetProperty("y").GetDouble(), 3);
        Assert.Equal(250, placement.GetProperty("endJambLine").GetProperty("start").GetProperty("x").GetDouble(), 3);
        Assert.Equal(102, placement.GetProperty("endJambLine").GetProperty("end").GetProperty("y").GetDouble(), 3);
        Assert.Equal(1, placement.GetProperty("alongVector").GetProperty("x").GetDouble(), 3);
        Assert.Equal(1, placement.GetProperty("normalVector").GetProperty("y").GetDouble(), 3);
        Assert.True(placement.GetProperty("confidence").GetDouble() > 0.5);
    }

    [Fact]
    public async Task PlacementExporter_IncludesOpeningAnchorOffsetsForDownstreamPlacement()
    {
        var document = new PlanDocument(
            "opening-placement-export",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-left-run", new PlanPoint(100, 100), new PlanPoint(220, 100)),
                        Wall("wall-right-run", new PlanPoint(250, 100), new PlanPoint(400, 100)),
                        new ArcPrimitive(new PlanPoint(220, 100), 30, 0, Math.PI / 2) { SourceId = "door-swing" }
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        result = result with
        {
            Calibration = new PlanCalibration(
                PlanMeasurementUnit.DrawingUnit,
                PlanMeasurementUnit.Millimeter,
                null,
                10,
                Confidence.High,
                Array.Empty<CalibrationEvidence>(),
                Array.Empty<CalibrationScaleGroup>())
        };

        var json = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var parsed = JsonDocument.Parse(json);
        var opening = parsed.RootElement.GetProperty("openings")[0];

        Assert.Equal(PlanPlacementExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("Anchored", opening.GetProperty("placementStatus").GetString());
        Assert.True(opening.GetProperty("reliability").GetProperty("readyForCoordinatePlacement").GetBoolean());
        var placement = opening.GetProperty("placement");
        Assert.Equal(220, placement.GetProperty("startPoint").GetProperty("x").GetDouble(), 3);
        Assert.Equal(100, placement.GetProperty("startPoint").GetProperty("y").GetDouble(), 3);
        Assert.Equal(250, placement.GetProperty("endPoint").GetProperty("x").GetDouble(), 3);
        Assert.Equal(100, placement.GetProperty("endPoint").GetProperty("y").GetDouble(), 3);
        Assert.Equal(2200, placement.GetProperty("startPointMillimeters").GetProperty("x").GetDouble(), 3);
        Assert.Equal(2500, placement.GetProperty("endPointMillimeters").GetProperty("x").GetDouble(), 3);
        Assert.Equal(120, placement.GetProperty("startOffsetDrawingUnits").GetDouble(), 3);
        Assert.Equal(150, placement.GetProperty("endOffsetDrawingUnits").GetDouble(), 3);
        Assert.Equal(30, placement.GetProperty("lengthDrawingUnits").GetDouble(), 3);
        Assert.Equal(4, placement.GetProperty("depthDrawingUnits").GetDouble(), 3);
        Assert.Equal(1200, placement.GetProperty("startOffsetMillimeters").GetDouble(), 3);
        Assert.Equal(1500, placement.GetProperty("endOffsetMillimeters").GetDouble(), 3);
        Assert.Equal(300, placement.GetProperty("lengthMillimeters").GetDouble(), 3);
        Assert.Equal(40, placement.GetProperty("depthMillimeters").GetDouble(), 3);
        Assert.Equal(4, placement.GetProperty("footprintCorners").GetArrayLength());
        Assert.Equal(4, placement.GetProperty("footprintCornersMillimeters").GetArrayLength());
        Assert.Equal(30, placement.GetProperty("footprintBounds").GetProperty("width").GetDouble(), 3);
        Assert.Equal(4, placement.GetProperty("footprintBounds").GetProperty("height").GetDouble(), 3);
        Assert.Equal(300, placement.GetProperty("footprintBoundsMillimeters").GetProperty("width").GetDouble(), 3);
        Assert.Equal(40, placement.GetProperty("footprintBoundsMillimeters").GetProperty("height").GetDouble(), 3);
        Assert.Equal(220, placement.GetProperty("startJambLine").GetProperty("start").GetProperty("x").GetDouble(), 3);
        Assert.Equal(250, placement.GetProperty("endJambLine").GetProperty("start").GetProperty("x").GetDouble(), 3);
        Assert.Equal(2200, placement.GetProperty("startJambLineMillimeters").GetProperty("start").GetProperty("x").GetDouble(), 3);
        Assert.Equal(2500, placement.GetProperty("endJambLineMillimeters").GetProperty("start").GetProperty("x").GetDouble(), 3);
        Assert.Equal(100, placement.GetProperty("referenceLine").GetProperty("start").GetProperty("x").GetDouble(), 3);
        Assert.Equal(400, placement.GetProperty("referenceLine").GetProperty("end").GetProperty("x").GetDouble(), 3);
        Assert.Equal(1000, placement.GetProperty("referenceLineMillimeters").GetProperty("start").GetProperty("x").GetDouble(), 3);
        Assert.Equal(4000, placement.GetProperty("referenceLineMillimeters").GetProperty("end").GetProperty("x").GetDouble(), 3);
        Assert.Equal(0, placement.GetProperty("crossWallOffsetMillimeters").GetDouble(), 3);

        var openingId = opening.GetProperty("id").GetString();
        var adjacentFragmentWalls = parsed.RootElement.GetProperty("walls")
            .EnumerateArray()
            .Where(wall => wall.GetProperty("solidSpans")
                .EnumerateArray()
                .Any(span => span.GetProperty("adjacentOpeningIds")
                    .EnumerateArray()
                    .Any(id => id.GetString() == openingId)))
            .OrderBy(wall => wall.GetProperty("centerLine").GetProperty("start").GetProperty("x").GetDouble())
            .ToArray();
        Assert.Equal(2, adjacentFragmentWalls.Length);
        Assert.All(adjacentFragmentWalls, wall =>
        {
            Assert.Equal(0, wall.GetProperty("openingCutouts").GetArrayLength());
            Assert.Single(wall.GetProperty("solidSpans").EnumerateArray());
        });
        Assert.Equal(100, adjacentFragmentWalls[0].GetProperty("solidSpans")[0].GetProperty("centerLine").GetProperty("start").GetProperty("x").GetDouble(), 3);
        Assert.Equal(220, adjacentFragmentWalls[0].GetProperty("solidSpans")[0].GetProperty("centerLine").GetProperty("end").GetProperty("x").GetDouble(), 3);
        Assert.Equal(250, adjacentFragmentWalls[1].GetProperty("solidSpans")[0].GetProperty("centerLine").GetProperty("start").GetProperty("x").GetDouble(), 3);
        Assert.Equal(400, adjacentFragmentWalls[1].GetProperty("solidSpans")[0].GetProperty("centerLine").GetProperty("end").GetProperty("x").GetDouble(), 3);

        var mergedHostWallId = "synthetic-merged-host-wall";
        var mergedHostResult = result with
        {
            Walls = new[]
            {
                new WallSegment(
                    mergedHostWallId,
                    1,
                    new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(400, 100)),
                    4,
                    Confidence.High)
                {
                    SourcePrimitiveIds = ["wall-left-run", "wall-right-run"],
                    Evidence = ["synthetic merged host wall for placement export cutout contract"]
                }
            },
            Openings = result.Openings
                .Select(item => item with
                {
                    HostWallIds = [mergedHostWallId],
                    Placement = item.Placement! with
                    {
                        HostWallId = mergedHostWallId,
                        AnchorWallIds = [mergedHostWallId]
                    }
                })
                .ToArray()
        };
        using var mergedParsed = JsonDocument.Parse(PlanPlacementJsonExporter.Serialize(
            mergedHostResult,
            new PlanPlacementJsonExportOptions { WriteIndented = false }));
        var cutWall = Assert.Single(mergedParsed.RootElement.GetProperty("walls").EnumerateArray());
        var cutout = Assert.Single(cutWall.GetProperty("openingCutouts").EnumerateArray());
        Assert.Equal(openingId, cutout.GetProperty("openingId").GetString());
        Assert.Equal(220, cutout.GetProperty("centerLine").GetProperty("start").GetProperty("x").GetDouble(), 3);
        Assert.Equal(250, cutout.GetProperty("centerLine").GetProperty("end").GetProperty("x").GetDouble(), 3);
        Assert.Equal(120, cutout.GetProperty("startOffsetDrawingUnits").GetDouble(), 3);
        Assert.Equal(150, cutout.GetProperty("endOffsetDrawingUnits").GetDouble(), 3);
        Assert.Equal(300, cutout.GetProperty("lengthMillimeters").GetDouble(), 3);

        var solidSpans = cutWall.GetProperty("solidSpans").EnumerateArray().ToArray();
        Assert.Equal(2, solidSpans.Length);
        Assert.Equal(100, solidSpans[0].GetProperty("centerLine").GetProperty("start").GetProperty("x").GetDouble(), 3);
        Assert.Equal(220, solidSpans[0].GetProperty("centerLine").GetProperty("end").GetProperty("x").GetDouble(), 3);
        Assert.Equal(250, solidSpans[1].GetProperty("centerLine").GetProperty("start").GetProperty("x").GetDouble(), 3);
        Assert.Equal(400, solidSpans[1].GetProperty("centerLine").GetProperty("end").GetProperty("x").GetDouble(), 3);
        Assert.All(solidSpans, span =>
            Assert.Contains(openingId, span.GetProperty("adjacentOpeningIds").EnumerateArray().Select(item => item.GetString())));

        var geoJson = PlanTraceGeoJsonExporter.Serialize(
            result,
            new PlanTraceGeoJsonExportOptions { WriteIndented = false });
        using var geoParsed = JsonDocument.Parse(geoJson);
        var openingFeature = Assert.Single(
            geoParsed.RootElement.GetProperty("features").EnumerateArray(),
            feature => feature.GetProperty("properties").GetProperty("featureType").GetString() == "opening");
        var geoProperties = openingFeature.GetProperty("properties");
        Assert.Equal(220, geoProperties.GetProperty("placementStartPoint")[0].GetDouble(), 3);
        Assert.Equal(100, geoProperties.GetProperty("placementStartPoint")[1].GetDouble(), 3);
        Assert.Equal(250, geoProperties.GetProperty("placementEndPoint")[0].GetDouble(), 3);
        Assert.Equal(100, geoProperties.GetProperty("placementEndPoint")[1].GetDouble(), 3);
        Assert.Equal(2, geoProperties.GetProperty("placementReferenceLine").GetArrayLength());
        Assert.Equal(4, geoProperties.GetProperty("placementFootprintCorners").GetArrayLength());
    }

    [Fact]
    public async Task PlacementExporter_FlagsIncoherentOpeningPlacementAsNotCoordinateReady()
    {
        var document = new PlanDocument(
            "opening-placement-incoherent-export",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-left-run", new PlanPoint(100, 100), new PlanPoint(220, 100)),
                        Wall("wall-right-run", new PlanPoint(250, 100), new PlanPoint(400, 100)),
                        new ArcPrimitive(new PlanPoint(220, 100), 30, 0, Math.PI / 2) { SourceId = "door-swing" }
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var opening = Assert.Single(result.Openings);
        Assert.NotNull(opening.Placement);
        var placement = opening.Placement!;
        var incoherentPlacement = placement with
        {
            LengthDrawingUnits = placement.LengthDrawingUnits + 90,
            HostWallEndParameter = 1.35
        };
        var incoherentResult = result with
        {
            Openings = new[] { opening with { Placement = incoherentPlacement } }
        };

        var placementJson = PlanPlacementJsonExporter.Serialize(
            incoherentResult,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var placementParsed = JsonDocument.Parse(placementJson);
        var exportedOpening = placementParsed.RootElement.GetProperty("openings")[0];
        var reliability = exportedOpening.GetProperty("reliability");
        var reasons = reliability.GetProperty("reasons")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Equal("Anchored", exportedOpening.GetProperty("placementStatus").GetString());
        Assert.False(reliability.GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.True(reliability.GetProperty("requiresReview").GetBoolean());
        Assert.Contains(
            reasons,
            reason => reason is not null
                && reason.Contains("placement offsets", StringComparison.OrdinalIgnoreCase)
                && reason.Contains("parameters", StringComparison.OrdinalIgnoreCase));

        var placementIssues = placementParsed.RootElement.GetProperty("issues").EnumerateArray().ToArray();
        var placementIssue = Assert.Single(
            placementIssues,
            issue => issue.GetProperty("code").GetString() == "placement.opening.placement_inconsistent"
                && issue.GetProperty("itemId").GetString() == opening.Id);
        Assert.Equal("Warning", placementIssue.GetProperty("severity").GetString());
        Assert.Equal("Anchored", placementIssue.GetProperty("properties").GetProperty("placementStatus").GetString());
        Assert.Contains(
            "inconsistent",
            placementIssue.GetProperty("properties").GetProperty("reasons").GetString(),
            StringComparison.OrdinalIgnoreCase);

        var placementImportReadiness = placementParsed.RootElement
            .GetProperty("summary")
            .GetProperty("importReadiness");
        Assert.Contains(
            "placement.opening.placement_inconsistent",
            placementImportReadiness.GetProperty("reviewIssueCodes").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            placementImportReadiness.GetProperty("recommendedActions").EnumerateArray(),
            item => item.GetString()?.Contains("opening placement spans", StringComparison.OrdinalIgnoreCase) == true);

        var directReadiness = PlanImportReadiness.FromScanResult(incoherentResult);
        Assert.Contains("placement.opening.placement_inconsistent", directReadiness.ReviewIssueCodes);

        var rebuiltRoutingLayer = PlanRoutingLayerBuilder.FromScanResult(incoherentResult);
        var passage = Assert.Single(rebuiltRoutingLayer.Passages);
        Assert.False(passage.ReadyForCoordinatePlacement);
        Assert.True(passage.RequiresReview);
        Assert.Contains(
            passage.ReviewReasons,
            reason => reason.Contains("inconsistent", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            passage.Evidence,
            item => item.Contains("not coordinate-ready", StringComparison.OrdinalIgnoreCase));

        var routingPassageExport = RoutingPassageExport.From(
            passage,
            new Dictionary<string, PrimitiveSourceExport>());
        Assert.Equal("Anchored", routingPassageExport.PlacementStatus);
        Assert.False(routingPassageExport.ReadyForCoordinatePlacement);
        Assert.True(routingPassageExport.RequiresReview);
        Assert.Contains(
            routingPassageExport.ReviewReasons,
            reason => reason.Contains("inconsistent", StringComparison.OrdinalIgnoreCase));

        var placementRoutingPassageExport = PlacementRoutingPassageExport.From(
            passage,
            incoherentResult.Calibration,
            new Dictionary<string, PrimitiveSourceExport>());
        Assert.Equal("Anchored", placementRoutingPassageExport.PlacementStatus);
        Assert.False(placementRoutingPassageExport.ReadyForCoordinatePlacement);
        Assert.True(placementRoutingPassageExport.RequiresReview);
        Assert.Contains(
            placementRoutingPassageExport.ReviewReasons,
            reason => reason.Contains("inconsistent", StringComparison.OrdinalIgnoreCase));

        var scanJson = PlanTraceJsonExporter.Serialize(incoherentResult);
        using var scanParsed = JsonDocument.Parse(scanJson);
        var review = Assert.Single(
            scanParsed.RootElement.GetProperty("reviewQueue").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == ScanReviewQueueKinds.OpeningReview);

        Assert.Equal("Warning", review.GetProperty("severity").GetString());
        Assert.Equal("Anchored", review.GetProperty("properties").GetProperty("placementStatus").GetString());
        Assert.Contains(
            "inconsistent",
            review.GetProperty("properties").GetProperty("reasons").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlacementExporter_NormalizesOpeningBoundsForSameBucketOffsetWalls()
    {
        var document = new PlanDocument(
            "offset-opening-placement-export",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-left-run", new PlanPoint(100, 100.6), new PlanPoint(220, 100.6)),
                        Wall("wall-right-run", new PlanPoint(250, 100.1), new PlanPoint(400, 100.1)),
                        new ArcPrimitive(new PlanPoint(220, 100.6), 30, 0, Math.PI / 2) { SourceId = "door-swing" }
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var opening = Assert.Single(result.Openings);

        Assert.False(opening.Bounds.IsEmpty);
        Assert.True(opening.Bounds.Width > 0);
        Assert.True(opening.Bounds.Height > 0);
        Assert.NotNull(opening.Placement);
        Assert.True(opening.Placement.LengthDrawingUnits > 0);

        var json = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var parsed = JsonDocument.Parse(json);
        var exportedBounds = parsed.RootElement.GetProperty("openings")[0].GetProperty("bounds");

        Assert.True(exportedBounds.GetProperty("width").GetDouble() > 0);
        Assert.True(exportedBounds.GetProperty("height").GetDouble() > 0);
    }

    [Fact]
    public async Task JsonExporter_IncludesPocketDoorOperation()
    {
        var document = new PlanDocument(
            "pocket-door-export",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-left-run", new PlanPoint(100, 100), new PlanPoint(220, 100)),
                        Wall("wall-right-run", new PlanPoint(255, 100), new PlanPoint(400, 100)),
                        ShortLine("pocket-track", "A-DOOR-POCKET", new PlanPoint(225, 104), new PlanPoint(252, 104))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var opening = parsed.RootElement.GetProperty("openings")[0];

        Assert.Equal("Door", opening.GetProperty("type").GetString());
        Assert.Equal("PocketSliding", opening.GetProperty("operation").GetString());
        Assert.Contains("pocket-track", opening.GetProperty("sourcePrimitiveIds").EnumerateArray().Select(item => item.GetString()));
    }

    private static LinePrimitive Wall(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-WALL",
            Source = Source(sourceId, "LINE", "A-WALL")
        };

    private static LinePrimitive ShortLine(string sourceId, string layer, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = layer,
            Source = Source(sourceId, "LINE", layer)
        };

    private static LinePrimitive DoorLeaf(string sourceId, PlanPoint start, PlanPoint end) =>
        ShortLine(sourceId, "A-DOOR", start, end);

    private static ArcPrimitive DoorArc(
        string sourceId,
        PlanPoint center,
        double radius,
        double startAngleRadians,
        double sweepAngleRadians) =>
        new(center, radius, startAngleRadians, sweepAngleRadians)
        {
            SourceId = sourceId,
            Layer = "A-DOOR",
            Source = Source(sourceId, "ARC", "A-DOOR")
        };

    private static PrimitiveSourceMetadata Source(string sourceId, string entityType, string layer) =>
        new()
        {
            SourceFormat = "test",
            SourceId = sourceId,
            EntityType = entityType,
            Layer = layer,
            DrawingSpace = SourceDrawingSpace.Model
        };
}
