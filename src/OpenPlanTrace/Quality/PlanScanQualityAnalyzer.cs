namespace OpenPlanTrace;

public static class PlanScanQualityAnalyzer
{
    private const double LowConfidenceThreshold = 0.5;
    private const double HighDimensionScaleSpreadThreshold = 2.0;

    public static PlanScanQualityReport Analyze(PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var detectors = CreateDetectorSummaries(result).ToArray();
        var issues = new List<PlanScanQualityIssue>();
        var evidence = new List<string>();
        var pageCount = result.Document.Pages.Count;
        var primitiveCount = result.Document.Pages.Sum(page => page.Primitives.Count);
        var structuralWalls = StructuralWallSelector.Select(result);
        var structuralWallIds = structuralWalls
            .Select(wall => wall.Id)
            .ToHashSet(StringComparer.Ordinal);
        var detectionCount = detectors.Sum(detector => detector.ItemCount);
        var score = 1.0;

        void AddIssue(
            string code,
            DiagnosticSeverity severity,
            string message,
            double penalty,
            IReadOnlyDictionary<string, string>? properties = null)
        {
            issues.Add(new PlanScanQualityIssue(
                code,
                severity,
                message,
                Confidence.High,
                properties ?? new Dictionary<string, string>()));
            score -= penalty;
        }

        if (pageCount == 0)
        {
            AddIssue("quality.no_pages", DiagnosticSeverity.Error, "Document contains no pages.", 1.0);
        }

        if (primitiveCount == 0)
        {
            AddIssue("quality.no_primitives", DiagnosticSeverity.Error, "Document contains no normalized primitives.", 0.45);
        }

        if (!result.SheetRegions.Any(region => region.Kind == RegionKind.MainFloorPlan))
        {
            AddIssue("quality.no_main_floorplan_region", DiagnosticSeverity.Warning, "No main floorplan region was detected.", 0.08);
        }

        if (structuralWalls.Count == 0)
        {
            AddIssue("quality.no_walls", DiagnosticSeverity.Warning, "No structural wall candidates were detected.", 0.18);
        }
        else if (!HasStructuralWallGraphTopology(result, structuralWallIds))
        {
            AddIssue("quality.incomplete_wall_graph", DiagnosticSeverity.Warning, "Structural walls were detected, but the wall graph has no usable topology.", 0.1);
        }

        if (structuralWalls.Count >= 3 && result.Rooms.Count == 0)
        {
            AddIssue("quality.no_rooms_from_walls", DiagnosticSeverity.Warning, "Structural walls were detected, but no rooms were solved from the wall graph.", 0.12);
        }

        if (!result.Calibration.HasReliableMeasurementScale)
        {
            AddIssue("quality.no_reliable_calibration", DiagnosticSeverity.Warning, "No reliable drawing-to-real-world calibration was selected.", 0.06);
        }

        var rasterNoPrimitiveDiagnostic = result.Diagnostics.Messages
            .FirstOrDefault(message => message.Code == "raster.extraction.no_primitives");
        if (rasterNoPrimitiveDiagnostic is not null)
        {
            AddIssue(
                "quality.raster_no_extracted_primitives",
                DiagnosticSeverity.Warning,
                "Raster source was loaded, but the extractor returned no text or linework evidence.",
                0.18,
                rasterNoPrimitiveDiagnostic.Properties);
        }

        var rasterLowConfidenceDiagnostic = result.Diagnostics.Messages
            .FirstOrDefault(message => message.Code == "raster.extraction.low_confidence");
        if (rasterLowConfidenceDiagnostic is not null)
        {
            AddIssue(
                "quality.raster_low_extraction_confidence",
                DiagnosticSeverity.Warning,
                "Raster source evidence is mostly low confidence and should be reviewed.",
                0.08,
                rasterLowConfidenceDiagnostic.Properties);
        }

        var pdfRasterOnlyDiagnostic = result.Diagnostics.Messages
            .FirstOrDefault(message => message.Code == "pdf.raster_image_only_pages");
        if (pdfRasterOnlyDiagnostic is not null)
        {
            AddIssue(
                "quality.pdf_raster_ocr_required",
                DiagnosticSeverity.Warning,
                "PDF appears to contain image-only page content; register a real raster/OCR adapter before trusting geometric detections.",
                0.2,
                pdfRasterOnlyDiagnostic.Properties);
        }

        if (result.MeasurementConsistency.OutlierCount > 0)
        {
            var checkedCount = Math.Max(1, result.MeasurementConsistency.CheckedCount);
            var outlierRatio = result.MeasurementConsistency.OutlierCount / (double)checkedCount;
            AddIssue(
                "quality.measurement_outliers",
                DiagnosticSeverity.Warning,
                "Some matched dimensions disagree with the selected calibration.",
                Math.Min(0.15, outlierRatio * 0.2),
                new Dictionary<string, string>
                {
                    ["checkedCount"] = result.MeasurementConsistency.CheckedCount.ToString(),
                    ["outlierCount"] = result.MeasurementConsistency.OutlierCount.ToString(),
                    ["outlierRatio"] = FormatRatio(outlierRatio),
                    ["metricImportImpact"] = result.MeasurementConsistency.HasBlockingOutliers ? "blocking" : "review"
                });
        }

        if (result.MeasurementConsistency.DimensionScaleSpreadRatio is >= HighDimensionScaleSpreadThreshold)
        {
            AddIssue(
                "quality.dimension_scale_spread_high",
                DiagnosticSeverity.Warning,
                "Matched dimensions imply widely different drawing scales and need review.",
                Math.Min(0.08, 0.025 + Math.Log(result.MeasurementConsistency.DimensionScaleSpreadRatio.Value) * 0.012),
                new Dictionary<string, string>
                {
                    ["checkedCount"] = result.MeasurementConsistency.CheckedCount.ToString(),
                    ["spreadRatio"] = FormatRatio(result.MeasurementConsistency.DimensionScaleSpreadRatio.Value),
                    ["medianDimensionMillimetersPerDrawingUnit"] = result.MeasurementConsistency.MedianDimensionMillimetersPerDrawingUnit is { } median
                        ? FormatRatio(median)
                        : string.Empty,
                    ["selectedMillimetersPerDrawingUnit"] = result.MeasurementConsistency.SelectedMillimetersPerDrawingUnit is { } selected
                        ? FormatRatio(selected)
                        : string.Empty
                });
        }

        var unassignedMeasurementScaleDiagnostics = result.Diagnostics.Messages
            .Where(message => message.Code == "measurement_scale.unassigned_detections")
            .ToArray();
        if (unassignedMeasurementScaleDiagnostics.Length > 0)
        {
            var unassignedCount = unassignedMeasurementScaleDiagnostics
                .Sum(message => ReadIntProperty(message, "unassignedMeasuredDetectionCount"));
            AddIssue(
                "quality.measurement_scale_provenance_missing",
                DiagnosticSeverity.Warning,
                "Some measured detections are on mixed-scale sheets and could not be assigned to a specific scale group.",
                Math.Min(0.12, 0.04 + (unassignedCount * 0.006)),
                new Dictionary<string, string>
                {
                    ["pageCount"] = unassignedMeasurementScaleDiagnostics
                        .Select(message => message.PageNumber)
                        .Where(pageNumber => pageNumber is not null)
                        .Distinct()
                        .Count()
                        .ToString(),
                    ["unassignedMeasuredDetectionCount"] = unassignedCount.ToString(),
                    ["diagnosticCount"] = unassignedMeasurementScaleDiagnostics.Length.ToString()
                });
        }

        if (result.ObjectGroups.Count > 0)
        {
            var reviewCount = result.ObjectGroups.Count(group => group.RequiresReview);
            if (reviewCount > 0)
            {
                AddIssue(
                    "quality.object_groups_require_review",
                    DiagnosticSeverity.Info,
                    "One or more repeated object/symbol groups still require user review.",
                    Math.Min(0.08, reviewCount / (double)result.ObjectGroups.Count * 0.12),
                    new Dictionary<string, string>
                    {
                        ["reviewGroupCount"] = reviewCount.ToString(),
                        ["objectGroupCount"] = result.ObjectGroups.Count.ToString()
                    });
            }
        }

        if (result.ObjectAggregates.Count > 0)
        {
            var reviewCount = result.ObjectAggregates.Count(aggregate => aggregate.RequiresReview);
            if (reviewCount > 0)
            {
                AddIssue(
                    "quality.object_aggregates_require_review",
                    DiagnosticSeverity.Info,
                    "One or more compound object aggregates still require user review.",
                    Math.Min(0.06, reviewCount / (double)result.ObjectAggregates.Count * 0.09),
                    new Dictionary<string, string>
                    {
                        ["reviewAggregateCount"] = reviewCount.ToString(),
                        ["objectAggregateCount"] = result.ObjectAggregates.Count.ToString()
                    });
            }
        }

        foreach (var risk in AnalyzeScannerRiskPatterns(result, evidence))
        {
            AddIssue(
                risk.Code,
                risk.Severity,
                risk.Message,
                risk.Penalty,
                risk.Properties);
        }

        if (result.Diagnostics.ErrorCount > 0)
        {
            AddIssue(
                "quality.diagnostic_errors",
                DiagnosticSeverity.Error,
                "The scan completed with diagnostic errors.",
                Math.Min(0.35, result.Diagnostics.ErrorCount * 0.08),
                new Dictionary<string, string> { ["errorCount"] = result.Diagnostics.ErrorCount.ToString() });
        }

        if (result.Diagnostics.WarningCount > 0)
        {
            AddIssue(
                "quality.diagnostic_warnings",
                DiagnosticSeverity.Warning,
                "The scan completed with diagnostic warnings.",
                Math.Min(0.12, result.Diagnostics.WarningCount * 0.015),
                new Dictionary<string, string> { ["warningCount"] = result.Diagnostics.WarningCount.ToString() });
        }

        var populatedDetectors = detectors.Where(detector => detector.ItemCount > 0).ToArray();
        var averageDetectorConfidence = populatedDetectors.Length == 0
            ? 0.0
            : populatedDetectors.Average(detector => detector.AverageConfidence.Value);
        if (populatedDetectors.Length > 0 && averageDetectorConfidence < 0.55)
        {
            AddIssue(
                "quality.low_detector_confidence",
                DiagnosticSeverity.Warning,
                "Average confidence across populated detector groups is low.",
                0.1,
                new Dictionary<string, string>
                {
                    ["averageDetectorConfidence"] = averageDetectorConfidence.ToString("0.###")
                });
        }

        var readinessScore = Math.Clamp(score, 0, 1);
        var confidenceScore = populatedDetectors.Length == 0
            ? readinessScore
            : (readinessScore * 0.65) + (averageDetectorConfidence * 0.35);
        var overallConfidence = new Confidence(confidenceScore);
        var grade = Grade(overallConfidence, issues);
        var requiresReview =
            grade is PlanScanQualityGrade.Poor or PlanScanQualityGrade.ReviewRequired
            || issues.Any(issue => issue.Severity == DiagnosticSeverity.Error)
            || issues.Any(RequiresQualityReview);

        evidence.Add($"readiness score {readinessScore:0.###}");
        if (populatedDetectors.Length > 0)
        {
            evidence.Add($"average detector confidence {averageDetectorConfidence:0.###}");
        }

        evidence.Add($"detectors with findings {populatedDetectors.Length}/{detectors.Length}");

        return new PlanScanQualityReport(
            overallConfidence,
            grade,
            requiresReview,
            pageCount,
            primitiveCount,
            detectionCount,
            detectors.Length,
            populatedDetectors.Length,
            result.Calibration.HasReliableMeasurementScale,
            result.Diagnostics.InfoCount,
            result.Diagnostics.WarningCount,
            result.Diagnostics.ErrorCount,
            detectors,
            issues,
            evidence);
    }

    private static int ReadIntProperty(PlanDiagnostic diagnostic, string propertyName) =>
        diagnostic.Properties.TryGetValue(propertyName, out var value)
            && int.TryParse(value, out var parsed)
                ? parsed
                : 0;

    private static bool MeasurementScaleNeedsReview(
        PlanScanResult result,
        double? measuredValue,
        string? measurementScaleGroupId) =>
        result.Calibration.HasReliableMeasurementScale
        && measuredValue is null
        && string.IsNullOrWhiteSpace(measurementScaleGroupId);

    private static bool HasRoomConnectivityEvidence(OpeningCandidate opening) =>
        opening.ConnectedRoomIds.Count > 0
        || opening.ConnectedRoomLabels.Count > 0
        || opening.ConnectedRoomLinks.Count > 0
        || opening.RoomAdjacencyIds.Count > 0;

    private static bool HasRoomConnectivityEvidence(RoutingPassage passage) =>
        passage.ConnectedRoomIds.Count > 0
        || passage.ConnectedRoomLabels.Count > 0
        || passage.ConnectedRoomLinks.Count > 0
        || passage.RoomAdjacencyIds.Count > 0;

    private static bool HasImportReadyRoomSideLinks(OpeningCandidate opening) =>
        HasImportReadyRoomSideLinks(opening.ConnectedRoomLinks, ExpectedRoomConnectionCount(opening));

    private static bool HasImportReadyRoomSideLinks(RoutingPassage passage) =>
        HasImportReadyRoomSideLinks(passage.ConnectedRoomLinks, ExpectedRoomConnectionCount(passage));

    private static bool HasImportReadyRoomSideLinks(
        IReadOnlyList<OpeningRoomConnection> links,
        int expectedRoomConnectionCount)
    {
        if (expectedRoomConnectionCount <= 0 || links.Count == 0)
        {
            return false;
        }

        var sideAwareLinks = links
            .Where(IsImportReadyRoomSideLink)
            .ToArray();
        if (sideAwareLinks.Length == 0)
        {
            return false;
        }

        if (expectedRoomConnectionCount <= 1)
        {
            return true;
        }

        var sideAwareRoomCount = sideAwareLinks
            .Select(link => link.RoomId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Count();
        return sideAwareRoomCount >= Math.Min(expectedRoomConnectionCount, 2)
            && HasOppositeRoomSides(sideAwareLinks);
    }

    private static bool HasOppositeRoomSides(IReadOnlyList<OpeningRoomConnection> links) =>
        links.Any(link => link.Side == OpeningRoomSide.PositiveNormalSide)
        && links.Any(link => link.Side == OpeningRoomSide.NegativeNormalSide);

    private static bool IsImportReadyRoomSideLink(OpeningRoomConnection link) =>
        !string.IsNullOrWhiteSpace(link.RoomId)
        && link.Side is OpeningRoomSide.PositiveNormalSide or OpeningRoomSide.NegativeNormalSide
        && link.RoomSidePoint is not null
        && link.NearestBoundaryPoint is not null
        && double.IsFinite(link.SignedDistanceFromOpening)
        && double.IsFinite(link.DistanceToOpening)
        && link.DistanceToOpening >= 0;

    private static int ExpectedRoomConnectionCount(OpeningCandidate opening) =>
        ExpectedRoomConnectionCount(
            opening.ConnectedRoomIds,
            opening.ConnectedRoomLabels,
            opening.RoomAdjacencyIds,
            opening.ConnectedRoomLinks);

    private static int ExpectedRoomConnectionCount(RoutingPassage passage) =>
        ExpectedRoomConnectionCount(
            passage.ConnectedRoomIds,
            passage.ConnectedRoomLabels,
            passage.RoomAdjacencyIds,
            passage.ConnectedRoomLinks);

    private static int ExpectedRoomConnectionCount(
        IReadOnlyList<string> roomIds,
        IReadOnlyList<string> roomLabels,
        IReadOnlyList<string> roomAdjacencyIds,
        IReadOnlyList<OpeningRoomConnection> roomLinks)
    {
        var roomIdCount = CountDistinctNonBlank(roomIds);
        var roomLabelCount = CountDistinctNonBlank(roomLabels);
        var roomLinkCount = CountDistinctNonBlank(roomLinks.Select(link => link.RoomId));
        var adjacencyImpliedCount = roomAdjacencyIds.Count > 0 ? 2 : 0;
        return Math.Max(Math.Max(roomIdCount, roomLabelCount), Math.Max(roomLinkCount, adjacencyImpliedCount));
    }

    private static int CountDistinctNonBlank(IEnumerable<string?> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Count();

    private static bool RequiresQualityReview(PlanScanQualityIssue issue) =>
        issue.Severity == DiagnosticSeverity.Warning
        && !string.Equals(issue.Code, "quality.diagnostic_warnings", StringComparison.Ordinal);

    private static PlanScanQualityGrade Grade(
        Confidence overallConfidence,
        IReadOnlyList<PlanScanQualityIssue> issues)
    {
        if (issues.Any(issue => issue.Severity == DiagnosticSeverity.Error) || overallConfidence.Value < 0.35)
        {
            return PlanScanQualityGrade.Poor;
        }

        if (overallConfidence.Value < 0.65)
        {
            return PlanScanQualityGrade.ReviewRequired;
        }

        return overallConfidence.Value < 0.85
            ? PlanScanQualityGrade.Usable
            : PlanScanQualityGrade.Strong;
    }

    private static IReadOnlyList<QualityRiskPattern> AnalyzeScannerRiskPatterns(
        PlanScanResult result,
        List<string> evidence)
    {
        var risks = new List<QualityRiskPattern>();
        var mainRegions = result.SheetRegions
            .Where(region => region.Kind == RegionKind.MainFloorPlan)
            .ToArray();
        var nonFloorplanRegions = result.SheetRegions
            .Where(region => region.Kind is RegionKind.TitleBlock or RegionKind.Notes or RegionKind.Dimensions or RegionKind.KeyPlan or RegionKind.Legend)
            .ToArray();
        var structuralWalls = StructuralWallSelector.Select(result);
        var structuralWallIds = structuralWalls
            .Select(wall => wall.Id)
            .ToHashSet(StringComparer.Ordinal);
        var structuralWallCount = structuralWalls.Count;

        var structuralDetectionCount = structuralWallCount + result.Rooms.Count + result.Openings.Count;
        if (nonFloorplanRegions.Length > 0 && structuralDetectionCount > 0)
        {
            var wallHits = CountRegionOverlaps(structuralWalls, nonFloorplanRegions, wall => wall.PageNumber, wall => wall.Bounds);
            var roomHits = CountRegionOverlaps(result.Rooms, nonFloorplanRegions, room => room.PageNumber, room => room.Bounds);
            var openingHits = CountRegionOverlaps(result.Openings, nonFloorplanRegions, opening => opening.PageNumber, opening => opening.Bounds);
            var structuralHits = wallHits + roomHits + openingHits;
            var hitRatio = structuralHits / (double)structuralDetectionCount;
            if (structuralHits >= 4 && hitRatio >= 0.08)
            {
                risks.Add(new QualityRiskPattern(
                    "quality.scan_risk.sheet_contamination",
                    DiagnosticSeverity.Warning,
                    "Structural detections overlap title-block, notes, dimensions, key-plan, or legend regions.",
                    Math.Min(0.09, 0.04 + hitRatio * 0.12),
                    new Dictionary<string, string>
                    {
                        ["structuralDetectionsInNonFloorplanRegions"] = structuralHits.ToString(),
                        ["wallHits"] = wallHits.ToString(),
                        ["roomHits"] = roomHits.ToString(),
                        ["openingHits"] = openingHits.ToString(),
                        ["structuralDetectionCount"] = structuralDetectionCount.ToString(),
                        ["hitRatio"] = FormatRatio(hitRatio)
                    }));
            }
        }

        if (mainRegions.Length > 0 && structuralWallCount >= 10)
        {
            var wallsOutsideMain = structuralWalls.Count(wall => !OverlapsAnyRegion(wall.Bounds, wall.PageNumber, mainRegions));
            var outsideRatio = wallsOutsideMain / (double)structuralWallCount;
            if (wallsOutsideMain >= 4 && outsideRatio >= 0.25)
            {
                risks.Add(new QualityRiskPattern(
                    "quality.scan_risk.geometry_outside_main_region",
                    DiagnosticSeverity.Warning,
                    "A significant share of wall candidates sits outside the detected main floorplan region.",
                    Math.Min(0.08, 0.03 + outsideRatio * 0.10),
                    new Dictionary<string, string>
                    {
                        ["wallsOutsideMainFloorplan"] = wallsOutsideMain.ToString(),
                        ["wallCount"] = structuralWallCount.ToString(),
                        ["outsideRatio"] = FormatRatio(outsideRatio),
                        ["mainFloorplanRegionCount"] = mainRegions.Length.ToString()
                    }));
            }
        }

        if (structuralWallCount >= 8)
        {
            var graph = AnalyzeWallGraphConnectivity(result.WallGraph, structuralWallIds);
            var wallIdsWithEdges = result.WallGraph.Edges
                .Where(edge => structuralWallIds.Contains(edge.WallId))
                .Select(edge => edge.WallId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            var isolatedWallCount = structuralWalls.Count(wall => !wallIdsWithEdges.Contains(wall.Id));
            var isolatedWallRatio = isolatedWallCount / (double)structuralWallCount;
            var edgeCoverageRatio = wallIdsWithEdges.Count / (double)structuralWallCount;
            var hasPoorGraphCoverage = isolatedWallRatio >= 0.35 || edgeCoverageRatio < 0.55;
            var hasDiffuseMajorComponents =
                graph.SignificantComponentCount >= 5
                && graph.LargestComponentNodeRatio < 0.35
                && graph.SmallComponentNodeRatio < 0.35;
            var fragmented =
                hasPoorGraphCoverage
                || hasDiffuseMajorComponents;
            if (fragmented)
            {
                risks.Add(new QualityRiskPattern(
                    "quality.scan_risk.fragmented_wall_graph",
                    DiagnosticSeverity.Warning,
                    "Wall detections appear fragmented or weakly connected, so room and opening topology may be unreliable.",
                    Math.Min(0.09, 0.04 + isolatedWallRatio * 0.10 + Math.Max(0, 0.70 - graph.LargestComponentNodeRatio) * 0.08),
                    new Dictionary<string, string>
                    {
                        ["wallCount"] = structuralWallCount.ToString(),
                        ["wallGraphNodeCount"] = graph.NodeCount.ToString(),
                        ["wallGraphEdgeCount"] = graph.EdgeCount.ToString(),
                        ["wallGraphComponentCount"] = graph.ComponentCount.ToString(),
                        ["largestComponentNodeRatio"] = FormatRatio(graph.LargestComponentNodeRatio),
                        ["significantComponentCount"] = graph.SignificantComponentCount.ToString(),
                        ["smallComponentCount"] = graph.SmallComponentCount.ToString(),
                        ["smallComponentNodeRatio"] = FormatRatio(graph.SmallComponentNodeRatio),
                        ["topComponentNodeCounts"] = graph.TopComponentNodeCounts,
                        ["isolatedWallCount"] = isolatedWallCount.ToString(),
                        ["isolatedWallRatio"] = FormatRatio(isolatedWallRatio),
                        ["edgeCoverageRatio"] = FormatRatio(edgeCoverageRatio)
                    }));
            }
        }

        var weakWalls = structuralWalls.Count(wall => wall.SourcePrimitiveIds.Count == 0 && wall.Evidence.Count == 0);
        var weakRooms = result.Rooms.Count(room => room.WallIds.Count == 0 && room.Evidence.Count == 0);
        var weakOpenings = result.Openings.Count(opening =>
            opening.SourcePrimitiveIds.Count == 0
            && opening.Evidence.Count == 0
            && opening.HostWallIds.Count == 0);
        var weakStructuralCount = weakWalls + weakRooms + weakOpenings;
        if (structuralDetectionCount >= 8)
        {
            var weakStructuralRatio = weakStructuralCount / (double)structuralDetectionCount;
            if (weakStructuralRatio >= 0.35)
            {
                risks.Add(new QualityRiskPattern(
                    "quality.scan_risk.weak_source_provenance",
                    DiagnosticSeverity.Warning,
                    "Many structural detections lack source primitive IDs or explicit evidence.",
                    Math.Min(0.08, 0.03 + weakStructuralRatio * 0.10),
                    new Dictionary<string, string>
                    {
                        ["weakStructuralDetections"] = weakStructuralCount.ToString(),
                        ["structuralDetectionCount"] = structuralDetectionCount.ToString(),
                        ["weakStructuralRatio"] = FormatRatio(weakStructuralRatio),
                        ["weakWallCount"] = weakWalls.ToString(),
                        ["weakRoomCount"] = weakRooms.ToString(),
                        ["weakOpeningCount"] = weakOpenings.ToString()
                    }));
            }
        }

        if (structuralWallCount > 0 && result.ObjectCandidates.Count >= Math.Max(50, structuralWallCount * 3))
        {
            var uncertainObjectCount = result.ObjectCandidates.Count(candidate =>
                candidate.Category is ObjectCategory.Unknown or ObjectCategory.GenericSymbol);
            var uncertainObjectRatio = uncertainObjectCount / (double)result.ObjectCandidates.Count;
            var reviewGroupRatio = result.ObjectGroups.Count == 0
                ? 0
                : result.ObjectGroups.Count(group => group.RequiresReview) / (double)result.ObjectGroups.Count;
            var objectToWallRatio = result.ObjectCandidates.Count / (double)Math.Max(1, structuralWallCount);
            if (uncertainObjectRatio >= 0.55 && (reviewGroupRatio >= 0.45 || objectToWallRatio >= 5.0))
            {
                risks.Add(new QualityRiskPattern(
                    "quality.scan_risk.object_noise_dominance",
                    DiagnosticSeverity.Warning,
                    "Dense unconfirmed symbol/object candidates may be competing with structural geometry.",
                    Math.Min(0.05, 0.025 + uncertainObjectRatio * 0.025),
                    new Dictionary<string, string>
                    {
                        ["objectCandidateCount"] = result.ObjectCandidates.Count.ToString(),
                        ["wallCount"] = structuralWallCount.ToString(),
                        ["objectToWallRatio"] = FormatRatio(objectToWallRatio),
                        ["uncertainObjectCount"] = uncertainObjectCount.ToString(),
                        ["uncertainObjectRatio"] = FormatRatio(uncertainObjectRatio),
                        ["reviewGroupRatio"] = FormatRatio(reviewGroupRatio)
                    }));
            }
        }

        if (result.Rooms.Count >= 2 && result.Openings.Count >= 4)
        {
            var roomConnectedOpenings = result.Openings.Count(HasRoomConnectivityEvidence);
            var roomConnectedRatio = roomConnectedOpenings / (double)result.Openings.Count;
            var hostedOpenings = result.Openings.Count(opening => opening.HostWallIds.Count > 0 || !string.IsNullOrWhiteSpace(opening.WallId));
            var hostedRatio = hostedOpenings / (double)result.Openings.Count;
            if (roomConnectedRatio < 0.25 && result.RoomAdjacencyGraph.Edges.Count == 0)
            {
                risks.Add(new QualityRiskPattern(
                    "quality.scan_risk.opening_room_mismatch",
                    DiagnosticSeverity.Warning,
                    "Rooms and openings were detected, but openings are not linked into room adjacency topology.",
                    0.04,
                    new Dictionary<string, string>
                    {
                        ["roomCount"] = result.Rooms.Count.ToString(),
                        ["openingCount"] = result.Openings.Count.ToString(),
                        ["roomConnectedOpeningCount"] = roomConnectedOpenings.ToString(),
                        ["roomConnectedOpeningRatio"] = FormatRatio(roomConnectedRatio),
                        ["hostedOpeningRatio"] = FormatRatio(hostedRatio),
                        ["roomAdjacencyEdgeCount"] = result.RoomAdjacencyGraph.Edges.Count.ToString()
                }));
            }
        }

        var connectedOpenings = result.Openings
            .Where(HasRoomConnectivityEvidence)
            .ToArray();
        if (result.Rooms.Count >= 2 && connectedOpenings.Length >= 2)
        {
            var sideAwareOpenings = connectedOpenings.Count(HasImportReadyRoomSideLinks);
            var sideAwareRatio = sideAwareOpenings / (double)connectedOpenings.Length;
            var twoSidedOpenings = connectedOpenings.Count(opening => ExpectedRoomConnectionCount(opening) >= 2);
            var oppositeSideOpenings = connectedOpenings.Count(opening =>
                ExpectedRoomConnectionCount(opening) >= 2
                && HasOppositeRoomSides(opening.ConnectedRoomLinks));

            if (sideAwareRatio < 0.75)
            {
                risks.Add(new QualityRiskPattern(
                    "quality.scan_risk.opening_room_side_links_incomplete",
                    DiagnosticSeverity.Warning,
                    "Openings are room-linked, but many lack side-aware room connection geometry for import-grade topology.",
                    Math.Min(0.05, 0.02 + (1 - sideAwareRatio) * 0.045),
                    new Dictionary<string, string>
                    {
                        ["roomCount"] = result.Rooms.Count.ToString(),
                        ["openingCount"] = result.Openings.Count.ToString(),
                        ["roomConnectedOpeningCount"] = connectedOpenings.Length.ToString(),
                        ["sideAwareConnectedOpeningCount"] = sideAwareOpenings.ToString(),
                        ["sideAwareConnectedOpeningRatio"] = FormatRatio(sideAwareRatio),
                        ["twoSidedOpeningCount"] = twoSidedOpenings.ToString(),
                        ["oppositeSideOpeningCount"] = oppositeSideOpenings.ToString(),
                        ["minimumSideAwareOpeningRatio"] = "0.75"
                    }));
            }
        }

        if (result.Rooms.Count > 0 && structuralWallCount >= 4)
        {
            var roomsWithoutWallLinks = result.Rooms
                .Where(room => room.WallIds.Count == 0)
                .ToArray();
            if (roomsWithoutWallLinks.Length > 0)
            {
                var detachedRoomRatio = roomsWithoutWallLinks.Length / (double)result.Rooms.Count;
                risks.Add(new QualityRiskPattern(
                    "quality.scan_risk.rooms_without_wall_links",
                    DiagnosticSeverity.Warning,
                    "Detected rooms lack linked wall-boundary evidence and need review before import-grade room topology is trusted.",
                    Math.Min(0.07, 0.025 + detachedRoomRatio * 0.06),
                    new Dictionary<string, string>
                    {
                        ["roomCount"] = result.Rooms.Count.ToString(),
                        ["structuralWallCount"] = structuralWallCount.ToString(),
                        ["roomsWithoutWallLinks"] = roomsWithoutWallLinks.Length.ToString(),
                        ["roomsWithoutWallLinksRatio"] = FormatRatio(detachedRoomRatio),
                        ["roomIds"] = string.Join(",", roomsWithoutWallLinks.Select(room => room.Id).Take(12)),
                        ["roomLabels"] = string.Join(
                            ",",
                            roomsWithoutWallLinks
                                .Select(room => room.Label)
                                .Where(label => !string.IsNullOrWhiteSpace(label))
                                .Take(12))
                    }));
            }
        }

        var roomLinkedRoutingPassages = result.RoutingLayer.Passages
            .Where(HasRoomConnectivityEvidence)
            .ToArray();
        if (result.Rooms.Count >= 2 && roomLinkedRoutingPassages.Length >= 2)
        {
            var sideAwarePassages = roomLinkedRoutingPassages.Count(HasImportReadyRoomSideLinks);
            var sideAwareRatio = sideAwarePassages / (double)roomLinkedRoutingPassages.Length;
            var twoSidedPassages = roomLinkedRoutingPassages.Count(passage => ExpectedRoomConnectionCount(passage) >= 2);
            var oppositeSidePassages = roomLinkedRoutingPassages.Count(passage =>
                ExpectedRoomConnectionCount(passage) >= 2
                && HasOppositeRoomSides(passage.ConnectedRoomLinks));

            if (sideAwareRatio < 0.75)
            {
                risks.Add(new QualityRiskPattern(
                    "quality.scan_risk.routing_passage_room_side_links_incomplete",
                    DiagnosticSeverity.Warning,
                    "Routing passages are room-linked, but many lack side-aware room connection geometry for import-grade path topology.",
                    Math.Min(0.04, 0.015 + (1 - sideAwareRatio) * 0.035),
                    new Dictionary<string, string>
                    {
                        ["roomCount"] = result.Rooms.Count.ToString(),
                        ["routingPassageCount"] = result.RoutingLayer.Passages.Count.ToString(),
                        ["roomLinkedRoutingPassageCount"] = roomLinkedRoutingPassages.Length.ToString(),
                        ["sideAwareRoutingPassageCount"] = sideAwarePassages.ToString(),
                        ["sideAwareRoutingPassageRatio"] = FormatRatio(sideAwareRatio),
                        ["twoSidedRoutingPassageCount"] = twoSidedPassages.ToString(),
                        ["oppositeSideRoutingPassageCount"] = oppositeSidePassages.ToString(),
                        ["minimumSideAwareRoutingPassageRatio"] = "0.75"
                    }));
            }
        }

        var surfacePatternWallOverlapDiagnostics = result.Diagnostics.Messages
            .Where(message => string.Equals(
                message.Code,
                "wall_graph.surface_pattern_wall_overlap.review",
                StringComparison.Ordinal))
            .ToArray();
        if (surfacePatternWallOverlapDiagnostics.Length > 0)
        {
            var structuralOverlapDiagnostics = surfacePatternWallOverlapDiagnostics
                .Where(message => message.Properties.TryGetValue("wallId", out var wallId)
                    && !string.IsNullOrWhiteSpace(wallId)
                    && structuralWallIds.Contains(wallId))
                .ToArray();
            var affectedWalls = structuralOverlapDiagnostics
                .Select(message => message.Properties.TryGetValue("wallId", out var wallId) ? wallId : null)
                .Where(wallId => !string.IsNullOrWhiteSpace(wallId))
                .Distinct(StringComparer.Ordinal)
                .Count();
            var affectedPatterns = structuralOverlapDiagnostics
                .Select(message => message.Properties.TryGetValue("surfacePatternId", out var patternId) ? patternId : null)
                .Where(patternId => !string.IsNullOrWhiteSpace(patternId))
                .Distinct(StringComparer.Ordinal)
                .Count();
            var maxWallOverlapRatio = structuralOverlapDiagnostics
                .Select(message => TryReadRatio(message, "wallOverlapRatio"))
                .DefaultIfEmpty(0)
                .Max();
            var overlapRatio = affectedWalls / (double)Math.Max(1, structuralWallCount);

            if (affectedWalls > 0)
            {
                risks.Add(new QualityRiskPattern(
                    "quality.scan_risk.surface_pattern_wall_overlap",
                    DiagnosticSeverity.Warning,
                    "Some structural wall candidates overlap dense non-structural surface/detail patterns and need review before topology import.",
                    Math.Min(0.08, 0.025 + overlapRatio * 0.10 + maxWallOverlapRatio * 0.02),
                    new Dictionary<string, string>
                    {
                        ["diagnosticCount"] = surfacePatternWallOverlapDiagnostics.Length.ToString(),
                        ["affectedWallCount"] = affectedWalls.ToString(),
                        ["affectedSurfacePatternCount"] = affectedPatterns.ToString(),
                        ["wallCount"] = structuralWallCount.ToString(),
                        ["affectedWallRatio"] = FormatRatio(overlapRatio),
                        ["maxWallOverlapRatio"] = FormatRatio(maxWallOverlapRatio)
                    }));
            }
        }

        if (result.Rooms.Count >= 2 && structuralWallCount >= 6)
        {
            var weakRoomBoundaryCount = result.Rooms.Count(room => room.WallIds.Count < 3 || room.Evidence.Count == 0);
            var weakRoomBoundaryRatio = weakRoomBoundaryCount / (double)result.Rooms.Count;
            if (weakRoomBoundaryRatio >= 0.50)
            {
                risks.Add(new QualityRiskPattern(
                    "quality.scan_risk.weak_room_boundaries",
                    DiagnosticSeverity.Warning,
                    "Many rooms have weak wall-boundary evidence.",
                    Math.Min(0.06, 0.025 + weakRoomBoundaryRatio * 0.05),
                    new Dictionary<string, string>
                    {
                        ["weakRoomBoundaryCount"] = weakRoomBoundaryCount.ToString(),
                        ["roomCount"] = result.Rooms.Count.ToString(),
                        ["weakRoomBoundaryRatio"] = FormatRatio(weakRoomBoundaryRatio)
                    }));
            }
        }

        evidence.Add(risks.Count == 0
            ? "professional scan-risk audit found no high-risk patterns"
            : $"professional scan-risk audit found {risks.Count} high-risk pattern(s)");
        return risks;
    }

    private static bool HasStructuralWallGraphTopology(
        PlanScanResult result,
        IReadOnlySet<string> structuralWallIds)
    {
        if (structuralWallIds.Count == 0
            || result.WallGraph.Nodes.Count == 0
            || result.WallGraph.Edges.Count == 0)
        {
            return false;
        }

        return result.WallGraph.Edges.Any(edge => structuralWallIds.Contains(edge.WallId));
    }

    private static IEnumerable<PlanDetectorQualitySummary> CreateDetectorSummaries(PlanScanResult result)
    {
        var structuralWalls = StructuralWallSelector.Select(result);
        yield return Summary(
            "layers",
            result.LayerAnalysis.Layers.Select(layer => new DetectorQualityItem(
                layer.Confidence,
                layer.Evidence.Count > 0,
                layer.LikelyCategory == LayerCategory.Unknown)));
        yield return Summary(
            "calibration",
            new[]
            {
                new DetectorQualityItem(
                    result.Calibration.Confidence,
                    result.Calibration.Evidence.Count > 0,
                    !result.Calibration.HasReliableMeasurementScale)
            });
        yield return Summary(
            "measurementConsistency",
            result.MeasurementConsistency.Checks.Select(check => new DetectorQualityItem(
                check.Confidence,
                check.Evidence.Count > 0,
                check.Status == MeasurementConsistencyStatus.Outlier)));
        yield return Summary(
            "titleBlocks",
            result.TitleBlocks.Select(titleBlock => new DetectorQualityItem(
                titleBlock.Confidence,
                titleBlock.SourcePrimitiveIds.Count > 0 || titleBlock.Fields.Any(field => field.Evidence.Count > 0),
                titleBlock.Fields.Count == 0)));
        yield return Summary(
            "dimensions",
            result.Dimensions.Select(dimension => new DetectorQualityItem(
                dimension.Confidence,
                dimension.Evidence.Count > 0,
                dimension.MillimetersPerDrawingUnit is null)));
        yield return Summary(
            "annotations",
            result.Annotations.Select(annotation => new DetectorQualityItem(
                annotation.Confidence,
                annotation.Evidence.Count > 0 || annotation.Items.Any(item => item.Evidence.Count > 0),
                false)));
        yield return Summary(
            "gridAxes",
            result.GridAxes.Select(axis => new DetectorQualityItem(
                axis.Confidence,
                axis.Evidence.Count > 0,
                string.IsNullOrWhiteSpace(axis.Label))));
        yield return Summary(
            "gridBaySpacings",
            result.GridBaySpacings.Select(bay => new DetectorQualityItem(
                bay.Confidence,
                bay.Evidence.Count > 0,
                MeasurementScaleNeedsReview(result, bay.DistanceMeters, bay.MeasurementScaleGroupId))));
        yield return Summary(
            "regions",
            result.SheetRegions.Select(region => new DetectorQualityItem(
                region.Confidence,
                region.SourcePrimitiveIds.Count > 0,
                region.Kind == RegionKind.Unknown)));
        yield return Summary(
            "surfacePatterns",
            result.SurfacePatterns.Select(pattern => new DetectorQualityItem(
                pattern.Confidence,
                pattern.Evidence.Count > 0 && pattern.SourcePrimitiveIds.Count > 0,
                pattern.RequiresReview)));
        yield return Summary(
            "walls",
            structuralWalls.Select(wall => new DetectorQualityItem(
                wall.Confidence,
                wall.Evidence.Count > 0,
                wall.SourcePrimitiveIds.Count == 0
                || MeasurementScaleNeedsReview(result, wall.LengthMeters, wall.MeasurementScaleGroupId))));
        yield return Summary(
            "wallGraph",
            result.WallGraph.Nodes
                .Select(node => new DetectorQualityItem(node.Confidence, node.Evidence.Count > 0, false))
                .Concat(result.WallGraph.Edges.Select(edge => new DetectorQualityItem(edge.Confidence, true, false)))
                .Concat(result.WallGraph.Components.Select(component => new DetectorQualityItem(
                    component.Confidence,
                    component.Evidence.Count > 0,
                    component.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment))));
        yield return Summary(
            "rooms",
            result.Rooms.Select(room => new DetectorQualityItem(
                room.Confidence,
                room.Evidence.Count > 0 || room.WallIds.Count > 0,
                string.IsNullOrWhiteSpace(room.Label)
                || MeasurementScaleNeedsReview(result, room.AreaSquareMeters, room.MeasurementScaleGroupId))));
        yield return Summary(
            "roomAdjacency",
            result.RoomAdjacencyGraph.Edges.Select(edge => new DetectorQualityItem(
                edge.Confidence,
                edge.Evidence.Count > 0,
                false)));
        yield return Summary(
            "roomClusters",
            result.RoomAdjacencyGraph.Clusters.Select(cluster => new DetectorQualityItem(
                cluster.Confidence,
                cluster.Evidence.Count > 0,
                false)));
        yield return Summary(
            "openings",
            result.Openings.Select(opening => new DetectorQualityItem(
                opening.Confidence,
                opening.Evidence.Count > 0,
                opening.HostWallIds.Count == 0
                || (HasRoomConnectivityEvidence(opening) && !HasImportReadyRoomSideLinks(opening))
                || opening.Operation == OpeningOperation.Unknown
                || MeasurementScaleNeedsReview(result, opening.WidthMillimeters, opening.MeasurementScaleGroupId))));
        yield return Summary(
            "objects",
            result.ObjectCandidates.Select(candidate => new DetectorQualityItem(
                candidate.Confidence,
                candidate.Evidence.Count > 0,
                candidate.Category == ObjectCategory.Unknown)));
        yield return Summary(
            "objectGroups",
            result.ObjectGroups.Select(group => new DetectorQualityItem(
                group.Confidence,
                group.Evidence.Count > 0,
                group.RequiresReview)));
        yield return Summary(
            "objectAggregates",
            result.ObjectAggregates.Select(aggregate => new DetectorQualityItem(
                aggregate.Confidence,
                aggregate.Evidence.Count > 0,
                aggregate.RequiresReview)));
    }

    private static PlanDetectorQualitySummary Summary(
        string name,
        IEnumerable<DetectorQualityItem> items)
    {
        var materialized = items.ToArray();
        if (materialized.Length == 0)
        {
            return new PlanDetectorQualitySummary(
                name,
                0,
                Confidence.None,
                Confidence.None,
                Confidence.None,
                0,
                0,
                0,
                Confidence.None,
                new[] { "detector produced no findings" });
        }

        var average = materialized.Average(item => item.Confidence.Value);
        var minimum = materialized.Min(item => item.Confidence.Value);
        var maximum = materialized.Max(item => item.Confidence.Value);
        var lowConfidenceCount = materialized.Count(item => item.Confidence.Value < LowConfidenceThreshold);
        var reviewRequiredCount = materialized.Count(item => item.RequiresReview);
        var evidenceBearingCount = materialized.Count(item => item.HasEvidence);
        var evidence = new List<string>
        {
            $"average confidence {average:0.###}",
            $"{evidenceBearingCount}/{materialized.Length} findings carry explicit evidence"
        };

        if (lowConfidenceCount > 0)
        {
            evidence.Add($"{lowConfidenceCount} findings below confidence {LowConfidenceThreshold:0.##}");
        }

        if (reviewRequiredCount > 0)
        {
            evidence.Add($"{reviewRequiredCount} findings require review");
        }

        return new PlanDetectorQualitySummary(
            name,
            materialized.Length,
            new Confidence(average),
            new Confidence(minimum),
            new Confidence(maximum),
            lowConfidenceCount,
            reviewRequiredCount,
            evidenceBearingCount,
            new Confidence(average),
            evidence);
    }

    private static int CountRegionOverlaps<T>(
        IEnumerable<T> items,
        IReadOnlyList<SheetRegion> regions,
        Func<T, int> pageNumber,
        Func<T, PlanRect> bounds) =>
        items.Count(item => OverlapsAnyRegion(bounds(item), pageNumber(item), regions));

    private static bool OverlapsAnyRegion(PlanRect bounds, int pageNumber, IReadOnlyList<SheetRegion> regions) =>
        regions.Any(region => region.PageNumber == pageNumber && OverlapsRegion(bounds, region.Bounds));

    private static bool OverlapsRegion(PlanRect bounds, PlanRect region)
    {
        if (bounds.IsEmpty || region.IsEmpty || !bounds.Intersects(region))
        {
            return false;
        }

        if (region.Contains(bounds.Center, 1.0))
        {
            return true;
        }

        var overlapArea = bounds.OverlapArea(region);
        if (overlapArea <= 0)
        {
            return false;
        }

        var boundsArea = Math.Max(1.0, bounds.Area);
        return overlapArea / boundsArea >= 0.18;
    }

    private static WallGraphConnectivity AnalyzeWallGraphConnectivity(
        WallGraph graph,
        IReadOnlySet<string> structuralWallIds)
    {
        var edges = graph.Edges
            .Where(edge => structuralWallIds.Contains(edge.WallId))
            .ToArray();
        var nodeIds = edges
            .SelectMany(edge => new[] { edge.FromNodeId, edge.ToNodeId })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var nodes = graph.Nodes
            .Where(node => nodeIds.Contains(node.Id))
            .ToArray();

        if (nodes.Length == 0)
        {
            return new WallGraphConnectivity(0, 0, 0, 0, 0, 0, 0, string.Empty);
        }

        var adjacency = nodes.ToDictionary(
            node => node.Id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (!adjacency.ContainsKey(edge.FromNodeId) || !adjacency.ContainsKey(edge.ToNodeId))
            {
                continue;
            }

            adjacency[edge.FromNodeId].Add(edge.ToNodeId);
            adjacency[edge.ToNodeId].Add(edge.FromNodeId);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var componentCount = 0;
        var largestComponent = 0;
        var componentSizes = new List<int>();
        foreach (var node in nodes)
        {
            if (!visited.Add(node.Id))
            {
                continue;
            }

            componentCount++;
            var size = 0;
            var stack = new Stack<string>();
            stack.Push(node.Id);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                size++;
                foreach (var next in adjacency[current])
                {
                    if (visited.Add(next))
                    {
                        stack.Push(next);
                    }
                }
            }

            largestComponent = Math.Max(largestComponent, size);
            componentSizes.Add(size);
        }

        componentSizes.Sort((left, right) => right.CompareTo(left));
        var significantThreshold = Math.Max(12, (int)Math.Ceiling(nodes.Length * 0.05));
        var significantComponentCount = componentSizes.Count(size => size >= significantThreshold);
        var smallComponentCount = componentSizes.Count(size => size <= 2);
        var smallComponentNodeCount = componentSizes.Where(size => size <= 2).Sum();
        var topComponentNodeCounts = string.Join(",", componentSizes.Take(5));

        return new WallGraphConnectivity(
            nodes.Length,
            edges.Length,
            componentCount,
            largestComponent / (double)nodes.Length,
            significantComponentCount,
            smallComponentCount,
            smallComponentNodeCount / (double)nodes.Length,
            topComponentNodeCounts);
    }

    private static string FormatRatio(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static double TryReadRatio(PlanDiagnostic diagnostic, string propertyName) =>
        diagnostic.Properties.TryGetValue(propertyName, out var text)
            && double.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
            ? Math.Clamp(value, 0, 1)
            : 0;

    private sealed record DetectorQualityItem(
        Confidence Confidence,
        bool HasEvidence,
        bool RequiresReview);

    private sealed record QualityRiskPattern(
        string Code,
        DiagnosticSeverity Severity,
        string Message,
        double Penalty,
        IReadOnlyDictionary<string, string> Properties);

    private sealed record WallGraphConnectivity(
        int NodeCount,
        int EdgeCount,
        int ComponentCount,
        double LargestComponentNodeRatio,
        int SignificantComponentCount,
        int SmallComponentCount,
        double SmallComponentNodeRatio,
        string TopComponentNodeCounts);
}
