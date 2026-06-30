namespace OpenPlanTrace;

internal sealed class WallGraphStage : IPipelineStage
{
    private const double MinOneEndpointMainStructuralMediumPairScore = 0.80;
    private const double MinOneEndpointMainStructuralMediumLength = 24.0;
    private const double MinCompactStructuralPairedWallPairScore = 0.68;
    private const int MaxSecondaryInteriorFragmentPromotionFragments = 12;
    private const int MaxSecondaryInteriorFragmentPromotionDuplicatePrimitives = 8;
    private const double MaxSecondaryInteriorFragmentPromotionGapRatio = 0.08;
    private const string ObjectLikeRoomBoundaryProtectionEvidence =
        "wall evidence: protected from object-like graph reclassification because a long clean fragment wall is confirmed by room-boundary support";

    private const string ObjectLikeLongCleanFragmentProtectionEvidence =
        "wall evidence: " + WallPlacementContextGuards.TrustedObjectLikeLongCleanFragmentInteriorEvidence;

    public string Name => "wall-graph";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var nodes = new List<NodeAccumulator>();
        var edges = new List<WallEdge>();
        var inferredNearTouchJunctionCount = 0;
        var normalizedCollinearJunctionCount = 0;
        var snappedEndpointGapCount = 0;
        var trimmedEndpointOverrunCount = 0;
        var suppressedEndpointOverrunTailEdgeCount = 0;
        var suppressedReviewJunctionPairCount = 0;
        var pairedEndpointSnapJunctionCount = 0;
        var trustedEndpointSnapJunctionCount = 0;
        var normalizedWallSegmentCount = 0;
        var endpointOverrunReviews = new List<EndpointOverrunReview>();
        var nearTouchTolerance = InferredNearTouchJunctionTolerance(context.Options);
        var normalizedWallsById = new Dictionary<string, WallSegment>(StringComparer.Ordinal);
        var graphInput = context.WallTopologyPreparation.HasPreparedSelection
            ? context.WallTopologyPreparation
            : WallTopologyPreparationStage.Prepare(context);
        var graphInputWallIds = graphInput.GraphWallIds
            .ToHashSet(StringComparer.Ordinal);
        var trustedReviewCoordinateRepairWallIds = TrustedReviewCoordinateRepairWallIds(context, graphInput)
            .ToHashSet(StringComparer.Ordinal);
        var automaticCoordinateRepairWallIds = graphInput.AutomaticCoordinateRepairWallIds
            .Concat(trustedReviewCoordinateRepairWallIds)
            .ToHashSet(StringComparer.Ordinal);
        var coordinateRepairSkippedWallIds = new HashSet<string>(StringComparer.Ordinal);
        var graphWalls = context.Walls
            .Where(wall => graphInputWallIds.Contains(wall.Id))
            .ToArray();
        graphWalls = OrthogonalizeNearAxisGraphWalls(
            graphWalls,
            context.Options,
            context.Calibration,
            normalizedWallsById,
            out var orthogonalizedWallCenterLineCount);
        var pointsByWallId = graphWalls.ToDictionary(
            wall => wall.Id,
            wall => new List<PlanPoint> { wall.CenterLine.Start, wall.CenterLine.End });

        foreach (var pageGroup in graphWalls.GroupBy(wall => wall.PageNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var walls = pageGroup.ToArray();
            var coordinateRepairSupportWalls = walls
                .Where(wall => automaticCoordinateRepairWallIds.Contains(wall.Id))
                .ToArray();
            var trustedEndpointSnaps = DetectTrustedEndpointToWallSnaps(
                walls,
                automaticCoordinateRepairWallIds,
                context,
                context.Options);
            trustedEndpointSnapJunctionCount += trustedEndpointSnaps.Count;
            var pairedEndpointSnaps = DetectPairedEndpointToWallSnaps(
                walls,
                automaticCoordinateRepairWallIds,
                context,
                context.Options);
            pairedEndpointSnapJunctionCount += pairedEndpointSnaps.Count;
            var endpointSnaps = trustedEndpointSnaps
                .Concat(pairedEndpointSnaps)
                .GroupBy(
                    snap => PointKey(snap.EndpointWallId, snap.HostWallId, snap.JunctionPoint, context.Options.WallSnapTolerance),
                    StringComparer.Ordinal)
                .Select(group => group.OrderBy(item => item.GapDistance).First())
                .ToArray();
            var pairedEndpointSnapPointsByWallId = BuildEndpointSnapPointLookup(endpointSnaps);
            foreach (var snap in endpointSnaps)
            {
                if (pointsByWallId.TryGetValue(snap.EndpointWallId, out var endpointWallPoints))
                {
                    AddPointIfMissing(endpointWallPoints, snap.JunctionPoint, context.Options.WallSnapTolerance);
                }

                if (pointsByWallId.TryGetValue(snap.HostWallId, out var hostWallPoints))
                {
                    AddPointIfMissing(hostWallPoints, snap.JunctionPoint, context.Options.WallSnapTolerance);
                }
            }

            for (var leftIndex = 0; leftIndex < walls.Length; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < walls.Length; rightIndex++)
                {
                    var leftWall = walls[leftIndex];
                    var rightWall = walls[rightIndex];
                    if (!CanUsePairForAutomaticJunctions(leftWall, rightWall, automaticCoordinateRepairWallIds))
                    {
                        if (WouldCreateAutomaticJunction(leftWall, rightWall, nearTouchTolerance, context.Options))
                        {
                            suppressedReviewJunctionPairCount++;
                        }

                        continue;
                    }

                    if (GeometryOperations.TryIntersect(
                        leftWall.CenterLine,
                        rightWall.CenterLine,
                        context.Options.WallSnapTolerance,
                        out var point))
                    {
                        pointsByWallId[leftWall.Id].Add(point);
                        pointsByWallId[rightWall.Id].Add(point);
                    }
                    else
                    {
                        foreach (var inferredPoint in InferNearTouchJunctions(
                            leftWall.CenterLine,
                            rightWall.CenterLine,
                            nearTouchTolerance,
                            context.Options.WallSnapTolerance))
                        {
                            pointsByWallId[leftWall.Id].Add(inferredPoint);
                            pointsByWallId[rightWall.Id].Add(inferredPoint);
                            inferredNearTouchJunctionCount++;
                        }

                        normalizedCollinearJunctionCount += AddCollinearWallJunctions(
                            leftWall,
                            rightWall,
                            context.Options.WallSnapTolerance,
                            context.Options.WallSnapTolerance,
                            pointsByWallId);
                    }
                }
            }

            foreach (var wall in walls)
            {
                var orderedPoints = pointsByWallId[wall.Id]
                    .OrderBy(point => wall.CenterLine.ProjectParameter(point))
                    .Aggregate(new List<PlanPoint>(), (unique, point) =>
                    {
                        if (unique.Count == 0 || unique[^1].DistanceTo(point) > context.Options.WallSnapTolerance)
                        {
                            unique.Add(point);
                        }

                        return unique;
                    });
                var wallSnappedEndpointGapCount = 0;
                var wallTrimmedEndpointOverrunCount = 0;
                IReadOnlyList<EndpointOverrunReview> wallEndpointOverrunReviews = Array.Empty<EndpointOverrunReview>();
                if (automaticCoordinateRepairWallIds.Contains(wall.Id))
                {
                    orderedPoints = SnapNearTouchEndpointGaps(
                        orderedPoints,
                        wall,
                        pointsByWallId,
                        coordinateRepairSupportWalls,
                        pairedEndpointSnapPointsByWallId,
                        context.Options,
                        out wallSnappedEndpointGapCount);
                    snappedEndpointGapCount += wallSnappedEndpointGapCount;
                    orderedPoints = TrimEndpointOverruns(
                        orderedPoints,
                        wall,
                        pointsByWallId,
                        coordinateRepairSupportWalls,
                        context.Options,
                        out wallTrimmedEndpointOverrunCount,
                        out wallEndpointOverrunReviews);
                    trimmedEndpointOverrunCount += wallTrimmedEndpointOverrunCount;
                    endpointOverrunReviews.AddRange(wallEndpointOverrunReviews);
                }
                else if (orderedPoints.Count > 2)
                {
                    coordinateRepairSkippedWallIds.Add(wall.Id);
                }

                if (wallSnappedEndpointGapCount > 0 || wallTrimmedEndpointOverrunCount > 0)
                {
                    var normalizedWall = NormalizeWallSegmentCenterLine(
                        wall,
                        orderedPoints,
                        context.Calibration,
                        wallTrimmedEndpointOverrunCount,
                        wallSnappedEndpointGapCount);
                    if (!normalizedWall.CenterLine.Equals(wall.CenterLine))
                    {
                        normalizedWallsById[wall.Id] = normalizedWall;
                        normalizedWallSegmentCount++;
                    }
                }

                for (var index = 1; index < orderedPoints.Count; index++)
                {
                    if (IsReviewedEndpointOverrunTail(
                        orderedPoints[index - 1],
                        orderedPoints[index],
                        wallEndpointOverrunReviews,
                        context.Options))
                    {
                        suppressedEndpointOverrunTailEdgeCount++;
                        continue;
                    }

                    var from = GetOrCreateNode(nodes, pageGroup.Key, orderedPoints[index - 1], context.Options);
                    var to = GetOrCreateNode(nodes, pageGroup.Key, orderedPoints[index], context.Options);

                    if (from.Id == to.Id)
                    {
                        continue;
                    }

                    edges.Add(
                        new WallEdge(
                            $"page:{pageGroup.Key}:edge:{edges.Count + 1}",
                            pageGroup.Key,
                            from.Id,
                            to.Id,
                            wall.Id,
                            wall.Confidence));

                    from.AddIncidentDirection(orderedPoints[index - 1], orderedPoints[index]);
                    to.AddIncidentDirection(orderedPoints[index], orderedPoints[index - 1]);
                }
            }
        }

        if (normalizedWallsById.Count > 0)
        {
            for (var index = 0; index < context.Walls.Count; index++)
            {
                if (normalizedWallsById.TryGetValue(context.Walls[index].Id, out var normalizedWall))
                {
                    context.Walls[index] = normalizedWall;
                }
            }

            SynchronizeWallEvidenceGeometry(context, normalizedWallsById);
        }

        var graphNodes = nodes
            .Select(node =>
            {
                var classification = ClassifyNode(node);
                return new WallNode(
                    node.Id,
                    node.PageNumber,
                    node.Position,
                    classification.Kind,
                    node.Degree,
                    classification.Directions,
                    node.Degree > 1 ? Confidence.High : Confidence.Medium,
                    classification.Evidence);
            })
            .ToArray();

        var graphEdges = edges.ToArray();
        var components = BuildComponents(graphNodes, graphEdges, graphWalls, context);
        RefineObjectLikeComponentWallEvidence(context, components);
        RefineShortIsolatedGraphWallEvidence(context, components, graphEdges, graphNodes);
        var endpointGapRepairCandidates = DetectUnresolvedEndpointGaps(
            graphNodes,
            graphEdges,
            components,
            graphWalls,
            automaticCoordinateRepairWallIds,
            context.Options,
            out var suppressedReviewEndpointGapCount).ToArray();
        var endpointOverrunRepairCandidates = DetectEndpointOverrunRepairCandidates(
            endpointOverrunReviews,
            graphNodes,
            components,
            graphWalls,
            context.Options);
        var repairCandidates = endpointGapRepairCandidates
            .Concat(endpointOverrunRepairCandidates)
            .OrderBy(candidate => candidate.PageNumber)
            .ThenBy(candidate => candidate.Kind)
            .ThenBy(candidate => candidate.GapDistance)
            .ThenBy(candidate => candidate.SourceNodeId, StringComparer.Ordinal)
            .ToArray();
        PromoteMainStructuralMediumWallEvidence(context, components, graphEdges, graphNodes, repairCandidates);
        PromoteSecondaryInteriorFragmentWallEvidence(context, components);
        context.WallGraph = new WallGraph(graphNodes, graphEdges, components, repairCandidates);

        AddComponentDiagnostics(context, components);
        AddSurfacePatternWallOverlapDiagnostics(context, components);
        AddEndpointGapDiagnostics(context, endpointGapRepairCandidates);
        AddEndpointOverrunDiagnostics(context, endpointOverrunRepairCandidates);
        AddGraphInputRejectionDiagnostics(context, graphInput.RejectedWalls);
        AddCoordinateRepairTrustGateDiagnostics(context, graphInput, graphWalls, coordinateRepairSkippedWallIds);
        AddTrustedReviewCoordinateRepairDiagnostics(context, graphInput, graphWalls, trustedReviewCoordinateRepairWallIds);
        AddCoordinateRepairSupportTrustDiagnostics(context, graphInput, graphWalls, trustedReviewCoordinateRepairWallIds);
        AddReviewJunctionTrustGateDiagnostics(
            context,
            graphInput,
            graphWalls,
            trustedReviewCoordinateRepairWallIds,
            suppressedReviewJunctionPairCount);
        AddEndpointGapReviewTrustDiagnostics(
            context,
            graphInput,
            graphWalls,
            trustedReviewCoordinateRepairWallIds,
            suppressedReviewEndpointGapCount);
        AddTopologyNormalizationDiagnostics(
            context,
            normalizedCollinearJunctionCount,
            snappedEndpointGapCount,
            trimmedEndpointOverrunCount,
            suppressedEndpointOverrunTailEdgeCount,
            normalizedWallSegmentCount,
            orthogonalizedWallCenterLineCount);

        if (inferredNearTouchJunctionCount > 0)
        {
            context.AddDiagnostic(
                "wall_graph.near_touch_junctions.inferred",
                DiagnosticSeverity.Info,
                Name,
                "Near-touch wall endpoints were connected into the wall graph.",
                properties: new Dictionary<string, string>
                {
                    ["inferredJunctionCount"] = inferredNearTouchJunctionCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["junctionTolerance"] = nearTouchTolerance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                });
        }

        if (pairedEndpointSnapJunctionCount > 0)
        {
            context.AddDiagnostic(
                "wall_graph.endpoint_gap.paired_support_snapped",
                DiagnosticSeverity.Info,
                Name,
                "Endpoint-to-wall gaps just beyond the normal safe snap distance were snapped because paired parallel wall-face evidence supported the same host wall.",
                properties: new Dictionary<string, string>
                {
                    ["pairedEndpointSnapCount"] = pairedEndpointSnapJunctionCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["singleEndpointSafeSnapTolerance"] = nearTouchTolerance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["pairedEndpointSnapTolerance"] = PairedEndpointSnapTolerance(context.Options).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["pairedEndpointSupportSeparation"] = PairedEndpointSupportSeparation(context.Options).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                });
        }

        if (trustedEndpointSnapJunctionCount > 0)
        {
            context.AddDiagnostic(
                "wall_graph.endpoint_gap.trusted_endpoint_snapped",
                DiagnosticSeverity.Info,
                Name,
                "Low-risk endpoint-to-wall gaps just beyond the normal safe snap distance were snapped because the host wall had trusted wall-body evidence.",
                properties: new Dictionary<string, string>
                {
                    ["trustedEndpointSnapCount"] = trustedEndpointSnapJunctionCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["singleEndpointSafeSnapTolerance"] = nearTouchTolerance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["trustedEndpointSnapTolerance"] = TrustedEndpointSnapTolerance(context.Options).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                });
        }

        if (graphWalls.Length > 0 && graphNodes.Length == 0)
        {
            context.AddDiagnostic(
                "wall_graph.empty",
                DiagnosticSeverity.Warning,
                Name,
                "Walls were detected, but no wall graph nodes were produced.",
                confidence: Confidence.Low);
        }

        return ValueTask.CompletedTask;
    }

    private static IReadOnlyList<string> TrustedReviewCoordinateRepairWallIds(
        ScanContext context,
        WallTopologyPreparation graphInput)
    {
        if (graphInput.ReviewGraphWallCount == 0 || context.WallEvidenceMap.WallAssessments.Count == 0)
        {
            return Array.Empty<string>();
        }

        var bandsByWallId = context.WallEvidenceMap.Bands
            .Where(band => !string.IsNullOrWhiteSpace(band.WallId))
            .GroupBy(band => band.WallId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var wallsById = context.Walls
            .ToDictionary(wall => wall.Id, StringComparer.Ordinal);

        return context.WallEvidenceMap.WallAssessments
            .Where(assessment => !string.IsNullOrWhiteSpace(assessment.WallId))
            .Where(assessment => graphInput.IsReviewGraphWall(assessment.WallId))
            .Where(assessment => wallsById.TryGetValue(assessment.WallId, out var wall)
                && IsTrustedReviewCoordinateRepairAssessment(assessment, wall))
            .Where(assessment => bandsByWallId.TryGetValue(assessment.WallId, out var bands)
                && bands.Any(IsTrustedReviewCoordinateRepairBand))
            .Select(assessment => assessment.WallId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsTrustedReviewCoordinateRepairAssessment(
        WallEvidenceWallAssessment assessment,
        WallSegment wall)
    {
        if (assessment.RejectedAsNoise
            || assessment.Decision != WallEvidenceDecision.Review
            || assessment.Category != WallEvidenceCategory.MediumWallBody
            || assessment.Confidence.Value < 0.80)
        {
            return false;
        }

        if (!assessment.Evidence.Any(item => item.Contains("parallel wall-face pair", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (assessment.Evidence.Any(IsHardRiskReviewWallEvidence))
        {
            return false;
        }

        if (assessment.Evidence.Any(IsSuspiciousButPotentialInteriorWallEvidence))
        {
            return wall.WallType == WallType.Interior
                || assessment.Evidence.Any(item => item.Contains("wall type interior", StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }

    private static bool IsTrustedReviewCoordinateRepairBand(WallEvidenceBand band) =>
        band.OverlapRatio >= 0.82
        && band.FaceSeparation >= 3.0
        && band.FaceSeparation <= 18.0
        && band.Confidence.Value >= 0.72;

    private static WallSegment[] OrthogonalizeNearAxisGraphWalls(
        IReadOnlyList<WallSegment> walls,
        ScannerOptions options,
        PlanCalibration calibration,
        Dictionary<string, WallSegment> normalizedWallsById,
        out int orthogonalizedWallCenterLineCount)
    {
        orthogonalizedWallCenterLineCount = 0;
        if (walls.Count == 0)
        {
            return Array.Empty<WallSegment>();
        }

        var result = new WallSegment[walls.Count];
        var wallsByPage = walls
            .GroupBy(wall => wall.PageNumber)
            .ToDictionary(group => group.Key, group => group.ToArray());

        for (var index = 0; index < walls.Count; index++)
        {
            var wall = walls[index];
            var pageWalls = wallsByPage[wall.PageNumber];
            if (TryOrthogonalizeNearAxisGraphWall(wall, pageWalls, options, calibration, out var normalizedWall))
            {
                result[index] = normalizedWall;
                normalizedWallsById[wall.Id] = normalizedWall;
                orthogonalizedWallCenterLineCount++;
            }
            else
            {
                result[index] = wall;
            }
        }

        return result;
    }

    private static bool TryOrthogonalizeNearAxisGraphWall(
        WallSegment wall,
        IReadOnlyList<WallSegment> pageWalls,
        ScannerOptions options,
        PlanCalibration calibration,
        out WallSegment normalizedWall)
    {
        normalizedWall = wall;
        if (wall.CenterLine.Length < Math.Max(options.MinWallLength, 1)
            || wall.Evidence.Any(IsHardRiskReviewWallEvidence)
            || wall.Evidence.Any(item =>
                item.Contains("non-orthogonal", StringComparison.OrdinalIgnoreCase)
                && !IsNearlyOrthogonalAngleEvidence(item)))
        {
            return false;
        }

        if (!TryResolveNearAxisOrientation(wall.CenterLine, options, out var orientation))
        {
            return false;
        }

        var axisCoordinate = ResolveSupportedOrthogonalAxisCoordinate(wall, pageWalls, orientation, options);
        var normalizedLine = orientation == OrthogonalAxis.Horizontal
            ? new PlanLineSegment(
                new PlanPoint(wall.CenterLine.Start.X, axisCoordinate),
                new PlanPoint(wall.CenterLine.End.X, axisCoordinate))
            : new PlanLineSegment(
                new PlanPoint(axisCoordinate, wall.CenterLine.Start.Y),
                new PlanPoint(axisCoordinate, wall.CenterLine.End.Y));

        if (normalizedLine.Length <= 1
            || wall.CenterLine.Start.DistanceTo(normalizedLine.Start) <= 0.001
            && wall.CenterLine.End.DistanceTo(normalizedLine.End) <= 0.001)
        {
            return false;
        }

        var scaleGroup = calibration.SelectMeasurementScaleGroup(
            wall.PageNumber,
            normalizedLine.Bounds.Inflate(Math.Max(wall.Thickness / 2.0, 0.5)),
            wall.SourceRegionId);
        var evidence = wall.Evidence
            .Append($"orthogonalized near-axis wall centerline to {orientation.ToString().ToLowerInvariant()} axis")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        normalizedWall = wall with
        {
            CenterLine = normalizedLine,
            Evidence = evidence,
            LengthMeters = calibration.ToMeters(normalizedLine.Length, scaleGroup) ?? wall.LengthMeters,
            ThicknessMillimeters = calibration.ToMillimeters(wall.Thickness, scaleGroup) ?? wall.ThicknessMillimeters,
            MeasurementScaleGroupId = scaleGroup?.Id ?? wall.MeasurementScaleGroupId
        };
        return true;
    }

    private static bool IsNearlyOrthogonalAngleEvidence(string evidence)
    {
        const double degreesTolerance = 2.75;
        var anglePrefix = evidence.IndexOf("angle ", StringComparison.OrdinalIgnoreCase);
        if (anglePrefix < 0)
        {
            return false;
        }

        var start = anglePrefix + "angle ".Length;
        var end = evidence.IndexOf(" degrees", start, StringComparison.OrdinalIgnoreCase);
        if (end <= start)
        {
            return false;
        }

        if (!double.TryParse(
            evidence[start..end],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var angleDegrees))
        {
            return false;
        }

        var normalized = Math.Abs(angleDegrees % 180.0);
        var distanceToOrthogonal = Math.Min(
            Math.Min(normalized, Math.Abs(normalized - 90.0)),
            Math.Abs(normalized - 180.0));
        return distanceToOrthogonal <= degreesTolerance;
    }

    private static bool TryResolveNearAxisOrientation(
        PlanLineSegment line,
        ScannerOptions options,
        out OrthogonalAxis orientation)
    {
        var dx = Math.Abs(line.End.X - line.Start.X);
        var dy = Math.Abs(line.End.Y - line.Start.Y);
        var skewTolerance = NearAxisSkewTolerance(options);
        const double angleToleranceRadians = 0.048;

        if (dx >= dy
            && dy <= skewTolerance
            && Math.Atan2(dy, Math.Max(dx, 0.001)) <= angleToleranceRadians)
        {
            orientation = OrthogonalAxis.Horizontal;
            return true;
        }

        if (dy > dx
            && dx <= skewTolerance
            && Math.Atan2(dx, Math.Max(dy, 0.001)) <= angleToleranceRadians)
        {
            orientation = OrthogonalAxis.Vertical;
            return true;
        }

        orientation = default;
        return false;
    }

    private static double ResolveSupportedOrthogonalAxisCoordinate(
        WallSegment wall,
        IReadOnlyList<WallSegment> pageWalls,
        OrthogonalAxis orientation,
        ScannerOptions options)
    {
        var supportedCoordinates = SupportedOrthogonalAxisCoordinates(wall, pageWalls, orientation, options)
            .Order()
            .ToArray();
        if (supportedCoordinates.Length > 0)
        {
            return DominantCoordinateOrMedian(supportedCoordinates, Math.Max(0.75, options.WallSnapTolerance * 0.25));
        }

        return orientation == OrthogonalAxis.Horizontal
            ? (wall.CenterLine.Start.Y + wall.CenterLine.End.Y) / 2.0
            : (wall.CenterLine.Start.X + wall.CenterLine.End.X) / 2.0;
    }

    private static IEnumerable<double> SupportedOrthogonalAxisCoordinates(
        WallSegment wall,
        IReadOnlyList<WallSegment> pageWalls,
        OrthogonalAxis orientation,
        ScannerOptions options)
    {
        var supportTolerance = Math.Max(options.WallSnapTolerance, NearAxisSkewTolerance(options));
        foreach (var other in pageWalls)
        {
            if (string.Equals(other.Id, wall.Id, StringComparison.Ordinal)
                || !TryResolveNearAxisOrientation(other.CenterLine, options, out var otherOrientation)
                || otherOrientation == orientation)
            {
                continue;
            }

            foreach (var endpoint in new[] { other.CenterLine.Start, other.CenterLine.End })
            {
                var parameter = wall.CenterLine.ProjectParameter(endpoint);
                var projected = wall.CenterLine.PointAt(Math.Clamp(parameter, 0, 1));
                if (parameter < -0.04
                    || parameter > 1.04
                    || endpoint.DistanceTo(projected) > supportTolerance)
                {
                    continue;
                }

                yield return orientation == OrthogonalAxis.Horizontal
                    ? endpoint.Y
                    : endpoint.X;
            }
        }
    }

    private static double NearAxisSkewTolerance(ScannerOptions options) =>
        Math.Max(0.75, Math.Min(Math.Max(options.WallSnapTolerance, 1.0), options.DefaultWallThickness * 0.75));

    private static double DominantCoordinateOrMedian(IReadOnlyList<double> sortedCoordinates, double tolerance)
    {
        if (sortedCoordinates.Count == 0)
        {
            return 0;
        }

        var median = Median(sortedCoordinates);
        var groups = new List<CoordinateGroup>();
        foreach (var coordinate in sortedCoordinates)
        {
            if (groups.Count == 0 || Math.Abs(coordinate - groups[^1].Last) > tolerance)
            {
                groups.Add(new CoordinateGroup(coordinate));
            }
            else
            {
                groups[^1].Add(coordinate);
            }
        }

        var dominant = groups
            .OrderByDescending(group => group.Count)
            .ThenBy(group => Math.Abs(group.Center - median))
            .ThenBy(group => group.Center)
            .First();

        return dominant.Count > 1 ? dominant.Center : median;
    }

    private static double Median(IReadOnlyList<double> sorted)
    {
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static bool IsHardRiskReviewWallEvidence(string evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return false;
        }

        return evidence.Contains("door", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("opening", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("outdoor", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("covered-area", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("fixture", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("object", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSuspiciousButPotentialInteriorWallEvidence(string evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return false;
        }

        return evidence.Contains("dimension", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("annotation", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("one distinct structural wall", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("only one structural endpoint support", StringComparison.OrdinalIgnoreCase);
    }

    private void AddReviewJunctionTrustGateDiagnostics(
        ScanContext context,
        WallTopologyPreparation graphInput,
        IReadOnlyList<WallSegment> graphWalls,
        IReadOnlySet<string> trustedReviewCoordinateRepairWallIds,
        int suppressedReviewJunctionPairCount)
    {
        if (suppressedReviewJunctionPairCount <= 0)
        {
            return;
        }

        var reviewWalls = graphWalls
            .Where(wall => graphInput.IsReviewGraphWall(wall.Id))
            .Where(wall => !trustedReviewCoordinateRepairWallIds.Contains(wall.Id))
            .OrderBy(wall => wall.PageNumber)
            .ThenBy(wall => wall.Id, StringComparer.Ordinal)
            .ToArray();
        if (reviewWalls.Length == 0)
        {
            return;
        }

        var sourcePrimitiveIds = reviewWalls
            .SelectMany(wall => wall.SourcePrimitiveIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        context.AddDiagnostic(
            "wall_graph.junctions.review_trust_gated",
            DiagnosticSeverity.Info,
            Name,
            "Review-required graph walls were prevented from splitting trusted wall graph topology; review them before accepting junctions.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: sourcePrimitiveIds,
            properties: new Dictionary<string, string>
            {
                ["suppressedJunctionPairCount"] = suppressedReviewJunctionPairCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["graphWallCount"] = graphInput.GraphWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["acceptedGraphWallCount"] = graphInput.AcceptedGraphWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reviewGraphWallCount"] = graphInput.ReviewGraphWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["unassessedGraphWallCount"] = graphInput.UnassessedGraphWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["automaticCoordinateRepairWallCount"] = graphInput.AutomaticCoordinateRepairWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["trustedReviewCoordinateRepairWallCount"] = trustedReviewCoordinateRepairWallIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["wallIds"] = string.Join(",", reviewWalls.Select(wall => wall.Id).Take(30))
            });
    }

    private void AddEndpointGapReviewTrustDiagnostics(
        ScanContext context,
        WallTopologyPreparation graphInput,
        IReadOnlyList<WallSegment> graphWalls,
        IReadOnlySet<string> trustedReviewCoordinateRepairWallIds,
        int suppressedReviewEndpointGapCount)
    {
        if (suppressedReviewEndpointGapCount <= 0)
        {
            return;
        }

        var reviewWalls = graphWalls
            .Where(wall => graphInput.IsReviewGraphWall(wall.Id))
            .Where(wall => !trustedReviewCoordinateRepairWallIds.Contains(wall.Id))
            .OrderBy(wall => wall.PageNumber)
            .ThenBy(wall => wall.Id, StringComparer.Ordinal)
            .ToArray();
        if (reviewWalls.Length == 0)
        {
            return;
        }

        var sourcePrimitiveIds = reviewWalls
            .SelectMany(wall => wall.SourcePrimitiveIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        context.AddDiagnostic(
            "wall_graph.endpoint_gap.review_candidate_trust_gated",
            DiagnosticSeverity.Info,
            Name,
            "Endpoint gap repair candidates involving review-required graph walls were suppressed; review wall evidence before suggesting snaps.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: sourcePrimitiveIds,
            properties: new Dictionary<string, string>
            {
                ["suppressedEndpointGapCandidateCount"] = suppressedReviewEndpointGapCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reviewGraphWallCount"] = graphInput.ReviewGraphWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["automaticCoordinateRepairWallCount"] = graphInput.AutomaticCoordinateRepairWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["trustedReviewCoordinateRepairWallCount"] = trustedReviewCoordinateRepairWallIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["wallIds"] = string.Join(",", reviewWalls.Select(wall => wall.Id).Take(30))
            });
    }

    private void AddTrustedReviewCoordinateRepairDiagnostics(
        ScanContext context,
        WallTopologyPreparation graphInput,
        IReadOnlyList<WallSegment> graphWalls,
        IReadOnlySet<string> trustedReviewCoordinateRepairWallIds)
    {
        if (trustedReviewCoordinateRepairWallIds.Count == 0)
        {
            return;
        }

        var trustedReviewWalls = graphWalls
            .Where(wall => trustedReviewCoordinateRepairWallIds.Contains(wall.Id))
            .OrderBy(wall => wall.PageNumber)
            .ThenBy(wall => wall.Id, StringComparer.Ordinal)
            .ToArray();
        var sourcePrimitiveIds = trustedReviewWalls
            .SelectMany(wall => wall.SourcePrimitiveIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        context.AddDiagnostic(
            "wall_graph.coordinate_repair.trusted_review_support",
            DiagnosticSeverity.Info,
            Name,
            "High-confidence review wall bodies were allowed to participate in wall graph coordinate repair while remaining review-required in exported evidence.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: sourcePrimitiveIds,
            properties: new Dictionary<string, string>
            {
                ["trustedReviewCoordinateRepairWallCount"] = trustedReviewCoordinateRepairWallIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reviewGraphWallCount"] = graphInput.ReviewGraphWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["automaticCoordinateRepairWallCount"] = graphInput.AutomaticCoordinateRepairWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["effectiveAutomaticCoordinateRepairWallCount"] = (graphInput.AutomaticCoordinateRepairWallCount + trustedReviewCoordinateRepairWallIds.Count).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["wallIds"] = string.Join(",", trustedReviewWalls.Select(wall => wall.Id).Take(30))
            });
    }

    private void AddCoordinateRepairSupportTrustDiagnostics(
        ScanContext context,
        WallTopologyPreparation graphInput,
        IReadOnlyList<WallSegment> graphWalls,
        IReadOnlySet<string> trustedReviewCoordinateRepairWallIds)
    {
        if (graphInput.ReviewGraphWallCount == 0
            || graphInput.AutomaticCoordinateRepairWallCount == 0)
        {
            return;
        }

        var reviewWalls = graphWalls
            .Where(wall => graphInput.IsReviewGraphWall(wall.Id))
            .Where(wall => !trustedReviewCoordinateRepairWallIds.Contains(wall.Id))
            .OrderBy(wall => wall.PageNumber)
            .ThenBy(wall => wall.Id, StringComparer.Ordinal)
            .ToArray();
        if (reviewWalls.Length == 0)
        {
            return;
        }

        var sourcePrimitiveIds = reviewWalls
            .SelectMany(wall => wall.SourcePrimitiveIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        context.AddDiagnostic(
            "wall_graph.coordinate_repair.review_support_excluded",
            DiagnosticSeverity.Info,
            Name,
            "Review-required graph walls were excluded as automatic coordinate repair support for trusted walls.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: sourcePrimitiveIds,
            properties: new Dictionary<string, string>
            {
                ["excludedSupportWallCount"] = reviewWalls.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["graphWallCount"] = graphInput.GraphWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["acceptedGraphWallCount"] = graphInput.AcceptedGraphWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reviewGraphWallCount"] = graphInput.ReviewGraphWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["unassessedGraphWallCount"] = graphInput.UnassessedGraphWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["automaticCoordinateRepairWallCount"] = graphInput.AutomaticCoordinateRepairWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["trustedReviewCoordinateRepairWallCount"] = trustedReviewCoordinateRepairWallIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["wallIds"] = string.Join(",", reviewWalls.Select(wall => wall.Id).Take(30))
            });
    }

    private void AddCoordinateRepairTrustGateDiagnostics(
        ScanContext context,
        WallTopologyPreparation graphInput,
        IReadOnlyList<WallSegment> graphWalls,
        IReadOnlySet<string> skippedWallIds)
    {
        if (skippedWallIds.Count == 0)
        {
            return;
        }

        var skippedWalls = graphWalls
            .Where(wall => skippedWallIds.Contains(wall.Id))
            .OrderBy(wall => wall.PageNumber)
            .ThenBy(wall => wall.Id, StringComparer.Ordinal)
            .ToArray();
        var sourcePrimitiveIds = skippedWalls
            .SelectMany(wall => wall.SourcePrimitiveIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        context.AddDiagnostic(
            "wall_graph.coordinate_repair.trust_gated",
            DiagnosticSeverity.Info,
            Name,
            "Automatic wall coordinate repair was skipped for review-required graph input walls.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: sourcePrimitiveIds,
            properties: new Dictionary<string, string>
            {
                ["skippedWallCount"] = skippedWallIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["acceptedGraphWallCount"] = graphInput.AcceptedGraphWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reviewGraphWallCount"] = graphInput.ReviewGraphWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["unassessedGraphWallCount"] = graphInput.UnassessedGraphWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["automaticCoordinateRepairWallCount"] = graphInput.AutomaticCoordinateRepairWallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["wallIds"] = string.Join(",", skippedWalls.Select(wall => wall.Id).Take(30))
            });
    }

    private void AddGraphInputRejectionDiagnostics(
        ScanContext context,
        IReadOnlyList<WallTopologyRejectedWall> excludedWalls)
    {
        if (excludedWalls.Count == 0)
        {
            return;
        }

        foreach (var pageGroup in excludedWalls.GroupBy(wall => wall.PageNumber).OrderBy(group => group.Key))
        {
            var pageRejectedWalls = pageGroup.ToArray();
            var excludedWallIds = pageRejectedWalls
                .Select(wall => wall.WallId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var sourcePrimitiveIds = pageRejectedWalls
                .SelectMany(wall => wall.SourcePrimitiveIds)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

            context.AddDiagnostic(
                "wall_graph.rejected_wall_evidence_excluded",
                DiagnosticSeverity.Info,
                Name,
                "Rejected Wall Evidence V2 candidates were withheld from wall graph topology while remaining available for QA/export review.",
                pageGroup.Key,
                confidence: Confidence.Medium,
                scope: DiagnosticScope.Page,
                sourcePrimitiveIds: sourcePrimitiveIds,
                properties: new Dictionary<string, string>
                {
                    ["excludedWallCount"] = excludedWallIds.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["doorOrOpeningSymbolCount"] = pageRejectedWalls.Count(wall => wall.Category == WallEvidenceCategory.DoorOrOpeningSymbol).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["surfacePatternDetailCount"] = pageRejectedWalls.Count(wall => wall.Category == WallEvidenceCategory.SurfacePatternDetail).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["dimensionOrAnnotationCount"] = pageRejectedWalls.Count(wall => wall.Category == WallEvidenceCategory.DimensionOrAnnotation).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["objectOrFixtureDetailCount"] = pageRejectedWalls.Count(wall => wall.Category == WallEvidenceCategory.ObjectOrFixtureDetail).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["wallIds"] = string.Join(",", excludedWallIds.Take(30))
                });
        }
    }

    private static IReadOnlyList<WallGraphComponent> BuildComponents(
        IReadOnlyList<WallNode> nodes,
        IReadOnlyList<WallEdge> edges,
        IReadOnlyList<WallSegment> walls,
        ScanContext context)
    {
        if (nodes.Count == 0 && walls.Count == 0)
        {
            return Array.Empty<WallGraphComponent>();
        }

        var nodesById = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var wallsById = walls.ToDictionary(wall => wall.Id, StringComparer.Ordinal);
        var incidentEdges = nodes.ToDictionary(
            node => node.Id,
            _ => new List<WallEdge>(),
            StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            if (incidentEdges.TryGetValue(edge.FromNodeId, out var fromEdges))
            {
                fromEdges.Add(edge);
            }

            if (incidentEdges.TryGetValue(edge.ToNodeId, out var toEdges))
            {
                toEdges.Add(edge);
            }
        }

        var rawComponents = new List<RawWallGraphComponent>();
        var visitedNodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes.OrderBy(node => node.PageNumber).ThenBy(node => node.Id, StringComparer.Ordinal))
        {
            if (!visitedNodeIds.Add(node.Id))
            {
                continue;
            }

            var nodeIds = new HashSet<string>(StringComparer.Ordinal) { node.Id };
            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            var wallIds = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>();
            stack.Push(node.Id);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                foreach (var edge in incidentEdges[current])
                {
                    edgeIds.Add(edge.Id);
                    if (!string.IsNullOrWhiteSpace(edge.WallId))
                    {
                        wallIds.Add(edge.WallId);
                    }

                    var next = string.Equals(edge.FromNodeId, current, StringComparison.Ordinal)
                        ? edge.ToNodeId
                        : edge.FromNodeId;
                    if (visitedNodeIds.Add(next))
                    {
                        nodeIds.Add(next);
                        stack.Push(next);
                    }
                }
            }

            rawComponents.Add(CreateRawComponent(
                node.PageNumber,
                wallIds,
                nodeIds,
                edgeIds,
                wallsById,
                nodesById,
                context.Options));
        }

        var wallIdsWithEdges = edges
            .Select(edge => edge.WallId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var wall in walls.OrderBy(wall => wall.PageNumber).ThenBy(wall => wall.Id, StringComparer.Ordinal))
        {
            if (wallIdsWithEdges.Contains(wall.Id))
            {
                continue;
            }

            rawComponents.Add(CreateRawComponent(
                wall.PageNumber,
                new[] { wall.Id },
                Array.Empty<string>(),
                Array.Empty<string>(),
                wallsById,
                nodesById,
                context.Options));
        }

        var components = new List<WallGraphComponent>();
        foreach (var pageGroup in rawComponents
            .Where(component => component.WallIds.Count > 0 || component.NodeIds.Count > 0)
            .GroupBy(component => component.PageNumber)
            .OrderBy(group => group.Key))
        {
            var pageBounds = context.Document.Pages.FirstOrDefault(page => page.Number == pageGroup.Key)?.Bounds
                ?? PlanRect.Empty;
            var mainRegion = context.SheetRegions
                .Where(region => region.PageNumber == pageGroup.Key && region.Kind == RegionKind.MainFloorPlan)
                .OrderByDescending(region => region.Bounds.Area)
                .FirstOrDefault();
            var mainBounds = mainRegion?.Bounds ?? PlanRect.Empty;
            if (mainBounds.IsEmpty || mainBounds.Width <= 0 || mainBounds.Height <= 0)
            {
                mainBounds = pageBounds;
            }

            var orderedRawComponents = pageGroup
                .OrderByDescending(component => component.DrawingLength)
                .ThenByDescending(component => component.WallIds.Count)
                .ThenBy(component => component.Bounds.Top)
                .ThenBy(component => component.Bounds.Left)
                .ToArray();
            var mainComponent = orderedRawComponents.FirstOrDefault(component => component.WallIds.Count >= 2 || component.EdgeIds.Count >= 2);
            var ordered = SplitContaminatedAnchoredPairedWallComponents(
                    orderedRawComponents,
                    mainComponent,
                    context,
                    wallsById,
                    nodesById,
                    edges)
                .OrderByDescending(component => component.DrawingLength)
                .ThenByDescending(component => component.WallIds.Count)
                .ThenBy(component => component.Bounds.Top)
                .ThenBy(component => component.Bounds.Left)
                .ToArray();
            var sequence = 1;
            foreach (var rawComponent in ordered)
            {
                var kind = ClassifyComponent(rawComponent, mainComponent, mainBounds, context);
                var exclusionReason = StructuralTopologyExclusionReason(
                    rawComponent,
                    kind,
                    mainComponent,
                    context);
                var excludedFromStructuralTopology = !string.IsNullOrWhiteSpace(exclusionReason);
                components.Add(
                    new WallGraphComponent(
                        $"page:{pageGroup.Key}:wall-component:{sequence++}",
                        pageGroup.Key,
                        kind,
                        rawComponent.Bounds,
                        rawComponent.WallIds,
                        rawComponent.NodeIds,
                        rawComponent.EdgeIds,
                        rawComponent.SourcePrimitiveIds,
                        rawComponent.DrawingLength,
                        ComponentConfidence(kind),
                        ComponentEvidence(rawComponent, kind, mainComponent, exclusionReason),
                        excludedFromStructuralTopology));
            }
        }

        return components
            .OrderBy(component => component.PageNumber)
            .ThenBy(component => component.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<RawWallGraphComponent> SplitContaminatedAnchoredPairedWallComponents(
        IReadOnlyList<RawWallGraphComponent> components,
        RawWallGraphComponent? mainComponent,
        ScanContext context,
        IReadOnlyDictionary<string, WallSegment> wallsById,
        IReadOnlyDictionary<string, WallNode> nodesById,
        IReadOnlyList<WallEdge> edges)
    {
        if (mainComponent is null || components.Count == 0)
        {
            return components;
        }

        var split = new List<RawWallGraphComponent>(components.Count);
        foreach (var component in components)
        {
            if (ReferenceEquals(component, mainComponent))
            {
                split.Add(component);
                continue;
            }

            var trustedWallIds = FindAnchoredTrustedPairedWallsInContaminatedComponent(
                    component,
                    mainComponent,
                    context,
                    wallsById)
                .ToArray();
            if (trustedWallIds.Length == 0)
            {
                split.Add(component);
                continue;
            }

            var trustedWallIdSet = trustedWallIds.ToHashSet(StringComparer.Ordinal);
            var remainingWallIds = component.WallIds
                .Where(id => !trustedWallIdSet.Contains(id))
                .ToArray();
            if (remainingWallIds.Length == 0)
            {
                split.Add(component);
                continue;
            }

            split.Add(CreateRawComponentForWallIds(
                component.PageNumber,
                trustedWallIds,
                wallsById,
                nodesById,
                edges,
                context.Options));
            split.Add(CreateRawComponentForWallIds(
                component.PageNumber,
                remainingWallIds,
                wallsById,
                nodesById,
                edges,
                context.Options));
        }

        return split;
    }

    private static IEnumerable<string> FindAnchoredTrustedPairedWallsInContaminatedComponent(
        RawWallGraphComponent component,
        RawWallGraphComponent mainComponent,
        ScanContext context,
        IReadOnlyDictionary<string, WallSegment> wallsById)
    {
        if (component.Bounds.IsEmpty
            || mainComponent.Bounds.IsEmpty
            || component.WallIds.Count < 2
            || component.WallIds.Count > 10
            || component.EdgeIds.Count == 0
            || component.PairedWallCount == 0
            || component.DiagonalWallCount == 0)
        {
            yield break;
        }

        var structuralNeighborhood = mainComponent.Bounds.Inflate(Math.Max(
            UnresolvedEndpointGapReviewTolerance(context.Options) * 2.0,
            context.Options.DefaultWallThickness * 10.0));
        if (!structuralNeighborhood.Intersects(component.Bounds))
        {
            yield break;
        }

        var assessmentsByWallId = context.WallEvidenceMap.WallAssessments
            .Where(assessment => !string.IsNullOrWhiteSpace(assessment.WallId))
            .GroupBy(assessment => assessment.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        foreach (var wallId in component.WallIds)
        {
            if (!wallsById.TryGetValue(wallId, out var wall)
                || !assessmentsByWallId.TryGetValue(wallId, out var assessment)
                || wall.DrawingLength < AnchoredSinglePairedWallBodyLength(context.Options)
                || !IsTrustedCompactStructuralPairedWall(assessment, wall, context.Options)
                || !HasEndpointAttachmentToMainComponent(wall, mainComponent, wallsById, context.Options))
            {
                continue;
            }

            yield return wallId;
        }
    }

    private static RawWallGraphComponent CreateRawComponentForWallIds(
        int pageNumber,
        IReadOnlyList<string> wallIds,
        IReadOnlyDictionary<string, WallSegment> wallsById,
        IReadOnlyDictionary<string, WallNode> nodesById,
        IReadOnlyList<WallEdge> edges,
        ScannerOptions options)
    {
        var wallIdSet = wallIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && wallsById.ContainsKey(id))
            .ToHashSet(StringComparer.Ordinal);
        var componentEdges = edges
            .Where(edge => !string.IsNullOrWhiteSpace(edge.WallId) && wallIdSet.Contains(edge.WallId))
            .ToArray();
        var nodeIds = componentEdges
            .SelectMany(edge => new[] { edge.FromNodeId, edge.ToNodeId })
            .Where(id => !string.IsNullOrWhiteSpace(id) && nodesById.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return CreateRawComponent(
            pageNumber,
            wallIdSet,
            nodeIds,
            componentEdges.Select(edge => edge.Id),
            wallsById,
            nodesById,
            options);
    }

    private static void RefineObjectLikeComponentWallEvidence(
        ScanContext context,
        IReadOnlyList<WallGraphComponent> components)
    {
        var objectLikeComponents = components
            .Where(component => component.Kind == WallGraphComponentKind.ObjectLikeIsland)
            .Where(component => component.ExcludedFromStructuralTopology)
            .Where(component => component.WallIds.Count > 0)
            .ToArray();
        if (objectLikeComponents.Length == 0 || context.WallEvidenceMap.WallAssessments.Count == 0)
        {
            return;
        }

        var componentByWallId = objectLikeComponents
            .SelectMany(component => component.WallIds.Select(wallId => new { WallId = wallId, Component = component }))
            .GroupBy(item => item.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Component, StringComparer.Ordinal);
        var wallsById = context.Walls
            .Where(wall => !string.IsNullOrWhiteSpace(wall.Id))
            .GroupBy(wall => wall.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var roomBoundaryWallIds = context.Rooms.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : RoomBoundaryWallReferenceBuilder
                .Build(context.Rooms, context.Walls, context.Options.WallSnapTolerance)
                .RoomIdsByWallId
                .Keys
                .ToHashSet(StringComparer.Ordinal);
        var refinedWallIds = new HashSet<string>(StringComparer.Ordinal);
        var protectedWallIds = new HashSet<string>(StringComparer.Ordinal);
        var protectionEvidenceByWallId = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var refinedAssessments = context.WallEvidenceMap.WallAssessments
            .Select(assessment =>
            {
                if (!componentByWallId.TryGetValue(assessment.WallId, out var component)
                    || assessment.RejectedAsNoise)
                {
                    return assessment;
                }

                if (wallsById.TryGetValue(assessment.WallId, out var wall)
                    && TryGetObjectLikeFragmentProtectionEvidence(
                        wall,
                        assessment,
                        roomBoundaryWallIds.Contains(assessment.WallId),
                        context.Options,
                        out var protectionEvidence))
                {
                    protectedWallIds.Add(assessment.WallId);
                    protectionEvidenceByWallId[assessment.WallId] = protectionEvidence;
                    return assessment with
                    {
                        Evidence = AppendEvidence(
                            assessment.Evidence,
                            protectionEvidence)
                    };
                }

                refinedWallIds.Add(assessment.WallId);
                return RefineObjectLikeComponentAssessment(assessment, component);
            })
            .ToArray();

        if (refinedWallIds.Count == 0 && protectedWallIds.Count == 0)
        {
            return;
        }

        var refinedSegments = context.WallEvidenceMap.Segments
            .Select(segment =>
            {
                if (segment.WallId is null)
                {
                    return segment;
                }

                if (refinedWallIds.Contains(segment.WallId))
                {
                    return segment with
                    {
                        Category = WallEvidenceCategory.ObjectOrFixtureDetail,
                        Confidence = new Confidence(Math.Max(segment.Confidence.Value, 0.74)),
                        Evidence = AppendEvidence(
                            segment.Evidence,
                            new[]
                            {
                                $"wall evidence: graph component {componentByWallId[segment.WallId].Id} classified as object-like linework"
                            })
                    };
                }

                if (protectedWallIds.Contains(segment.WallId))
                {
                    return segment with
                    {
                        Evidence = AppendEvidence(
                            segment.Evidence,
                            protectionEvidenceByWallId[segment.WallId])
                    };
                }

                return segment;
            })
            .ToArray();

        context.WallEvidenceMap = context.WallEvidenceMap with
        {
            Segments = refinedSegments,
            WallAssessments = refinedAssessments
        };

        for (var index = 0; index < context.Walls.Count; index++)
        {
            var wall = context.Walls[index];
            if (!componentByWallId.TryGetValue(wall.Id, out var component))
            {
                continue;
            }

            if (refinedWallIds.Contains(wall.Id))
            {
                context.Walls[index] = wall with
                {
                    Evidence = AppendEvidence(
                        wall.Evidence,
                        new[]
                        {
                            $"wall evidence assessment: {WallEvidenceCategory.ObjectOrFixtureDetail} / rejected / graph component {component.Id}"
                        })
                };
            }
            else if (protectedWallIds.Contains(wall.Id))
            {
                context.Walls[index] = wall with
                {
                    Evidence = AppendEvidence(
                        wall.Evidence,
                        protectionEvidenceByWallId[wall.Id])
                };
            }
        }

        if (refinedWallIds.Count > 0)
        {
            var refinedSourceIds = objectLikeComponents
                .Where(component => component.WallIds.Any(refinedWallIds.Contains))
                .SelectMany(component => component.SourcePrimitiveIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            context.AddDiagnostic(
                "wall_evidence.object_like_components_reclassified",
                DiagnosticSeverity.Info,
                "wall-graph",
                $"{refinedWallIds.Count} wall evidence assessment(s) were reclassified as object/fixture detail from object-like wall graph components.",
                confidence: Confidence.Medium,
                scope: DiagnosticScope.Detection,
                sourcePrimitiveIds: refinedSourceIds,
                properties: new Dictionary<string, string>
                {
                    ["reclassifiedWallCount"] = refinedWallIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["objectLikeComponentCount"] = objectLikeComponents.Count(component => component.WallIds.Any(refinedWallIds.Contains)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["componentIds"] = string.Join(",", objectLikeComponents
                        .Where(component => component.WallIds.Any(refinedWallIds.Contains))
                        .Select(component => component.Id)
                        .Take(20))
                });
        }

        if (protectedWallIds.Count > 0)
        {
            context.AddDiagnostic(
                "wall_evidence.object_like_room_boundary_fragments_protected",
                DiagnosticSeverity.Info,
                "wall-graph",
                $"{protectedWallIds.Count} long clean fragment wall(s) were protected from object-like graph reclassification.",
                confidence: Confidence.Medium,
                scope: DiagnosticScope.Detection,
                sourcePrimitiveIds: context.Walls
                    .Where(wall => protectedWallIds.Contains(wall.Id))
                    .SelectMany(wall => wall.SourcePrimitiveIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                properties: new Dictionary<string, string>
                {
                    ["protectedWallCount"] = protectedWallIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["wallIds"] = string.Join(",", protectedWallIds.OrderBy(id => id, StringComparer.Ordinal).Take(20))
                });
        }
    }

    private static bool TryGetObjectLikeFragmentProtectionEvidence(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        bool hasRoomBoundaryReference,
        ScannerOptions options,
        out IReadOnlyList<string> protectionEvidence)
    {
        if (IsProtectedObjectLikeRoomBoundaryFragmentAssessment(
            wall,
            assessment,
            hasRoomBoundaryReference,
            options))
        {
            protectionEvidence = new[] { ObjectLikeRoomBoundaryProtectionEvidence };
            return true;
        }

        if (IsProtectedObjectLikeLongCleanFragmentAssessment(wall, assessment))
        {
            protectionEvidence = new[] { ObjectLikeLongCleanFragmentProtectionEvidence };
            return true;
        }

        protectionEvidence = Array.Empty<string>();
        return false;
    }

    private static bool IsProtectedObjectLikeRoomBoundaryFragmentAssessment(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        bool hasRoomBoundaryReference,
        ScannerOptions options)
    {
        if (!hasRoomBoundaryReference
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category is not (WallEvidenceCategory.MediumWallBody
                or WallEvidenceCategory.StrongWallBody
                or WallEvidenceCategory.RecoveredWallBody)
            || wall.DetectionKind != WallDetectionKind.FragmentMerged
            || wall.PairEvidence is not null
            || wall.FragmentEvidence is not { RequiresGeometryReview: false } fragmentEvidence
            || wall.DrawingLength < Math.Max(72.0, options.MinWallLength * 3.0)
            || wall.Confidence.Value < 0.78
            || assessment.Confidence.Value < 0.72)
        {
            return false;
        }

        if (fragmentEvidence.GapRatio > 0.015
            || fragmentEvidence.TotalHealedGap > Math.Max(3.0, wall.Thickness * 0.6)
            || fragmentEvidence.MaxHealedGap > Math.Max(3.0, wall.Thickness * 0.6))
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();
        if (!evidence.Any(item => item.Contains("merged collinear wall fragments", StringComparison.OrdinalIgnoreCase))
            || !evidence.Any(item => item.Contains("supported wall evidence inside exterior envelope", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !evidence.Any(item =>
            item.Contains("outdoor", StringComparison.OrdinalIgnoreCase)
            || item.Contains("terrace", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered-area", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered entry", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered-entry", StringComparison.OrdinalIgnoreCase)
            || item.Contains("overbygd", StringComparison.OrdinalIgnoreCase)
            || item.Contains("canopy", StringComparison.OrdinalIgnoreCase)
            || item.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
            || item.Contains("object/fixture", StringComparison.OrdinalIgnoreCase)
            || item.Contains("fixture detail", StringComparison.OrdinalIgnoreCase)
            || item.Contains("repeated short detail", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door/opening", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door swing", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door leaf", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door arc", StringComparison.OrdinalIgnoreCase)
            || item.Contains("stair", StringComparison.OrdinalIgnoreCase)
            || item.Contains("railing", StringComparison.OrdinalIgnoreCase)
            || item.Contains("dimension-like", StringComparison.OrdinalIgnoreCase)
            || item.Contains("classified Dimension", StringComparison.OrdinalIgnoreCase)
            || item.Contains("dimension/annotation", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsProtectedObjectLikeLongCleanFragmentAssessment(
        WallSegment wall,
        WallEvidenceWallAssessment assessment)
    {
        if (assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category != WallEvidenceCategory.MediumWallBody
            || wall.DetectionKind != WallDetectionKind.FragmentMerged
            || wall.PairEvidence is not null
            || wall.FragmentEvidence is not { RequiresGeometryReview: false } fragmentEvidence
            || wall.DrawingLength < 120.0
            || wall.Confidence.Value < 0.84
            || assessment.Confidence.Value < 0.82)
        {
            return false;
        }

        var uniqueSourcePrimitiveCount = Math.Max(0, wall.SourcePrimitiveIds.Count - fragmentEvidence.DuplicatePrimitiveCount);
        var fragmentCount = Math.Max(fragmentEvidence.FragmentCount, uniqueSourcePrimitiveCount);
        if (fragmentCount is < 2 or > 12
            || fragmentEvidence.DuplicatePrimitiveCount > 8
            || fragmentEvidence.GapRatio > 0.001
            || fragmentEvidence.TotalHealedGap > 0.001
            || fragmentEvidence.MaxHealedGap > 0.001)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();
        if (!evidence.Any(item => item.Contains("merged collinear wall fragments", StringComparison.OrdinalIgnoreCase))
            || !evidence.Any(item => item.Contains("supported wall evidence inside exterior envelope", StringComparison.OrdinalIgnoreCase))
            || !evidence.Any(item => item.Contains("one trusted structural endpoint", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !evidence.Any(item =>
            item.Contains("outdoor", StringComparison.OrdinalIgnoreCase)
            || item.Contains("terrace", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered-area", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered entry", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered-entry", StringComparison.OrdinalIgnoreCase)
            || item.Contains("overbygd", StringComparison.OrdinalIgnoreCase)
            || item.Contains("canopy", StringComparison.OrdinalIgnoreCase)
            || item.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
            || item.Contains("object/fixture", StringComparison.OrdinalIgnoreCase)
            || item.Contains("fixture detail", StringComparison.OrdinalIgnoreCase)
            || item.Contains("repeated short detail", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door/opening", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door swing", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door leaf", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door arc", StringComparison.OrdinalIgnoreCase)
            || item.Contains("stair", StringComparison.OrdinalIgnoreCase)
            || item.Contains("railing", StringComparison.OrdinalIgnoreCase)
            || item.Contains("dimension-like", StringComparison.OrdinalIgnoreCase)
            || item.Contains("classified Dimension", StringComparison.OrdinalIgnoreCase)
            || item.Contains("dimension/annotation", StringComparison.OrdinalIgnoreCase));
    }

    private static void RefineShortIsolatedGraphWallEvidence(
        ScanContext context,
        IReadOnlyList<WallGraphComponent> components,
        IReadOnlyList<WallEdge> graphEdges,
        IReadOnlyList<WallNode> graphNodes)
    {
        if (context.WallEvidenceMap.WallAssessments.Count == 0 || components.Count == 0)
        {
            return;
        }

        var isolatedComponents = components
            .Where(component => component.Kind == WallGraphComponentKind.IsolatedFragment)
            .Where(component => !component.ExcludedFromStructuralTopology)
            .Where(component => component.WallIds.Count > 0)
            .ToArray();
        if (isolatedComponents.Length == 0)
        {
            return;
        }

        var componentByWallId = isolatedComponents
            .SelectMany(component => component.WallIds.Select(wallId => new { WallId = wallId, Component = component }))
            .GroupBy(item => item.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Component, StringComparer.Ordinal);
        var wallsById = context.Walls.ToDictionary(wall => wall.Id, StringComparer.Ordinal);
        var edgesByWallId = graphEdges
            .GroupBy(edge => edge.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var nodeDegreeById = graphNodes.ToDictionary(node => node.Id, node => node.Degree, StringComparer.Ordinal);
        var reviewWallIds = new HashSet<string>(StringComparer.Ordinal);
        var supportedEndpointByWallId = new Dictionary<string, int>(StringComparer.Ordinal);

        var refinedAssessments = context.WallEvidenceMap.WallAssessments
            .Select(assessment =>
            {
                if (!componentByWallId.TryGetValue(assessment.WallId, out var component)
                    || !wallsById.TryGetValue(assessment.WallId, out var wall)
                    || !ShouldReviewShortIsolatedGraphWall(
                        assessment,
                        wall,
                        edgesByWallId.TryGetValue(assessment.WallId, out var wallEdges) ? wallEdges : Array.Empty<WallEdge>(),
                        nodeDegreeById,
                        context.Options,
                        out var supportedEndpointCount))
                {
                    return assessment;
                }

                reviewWallIds.Add(assessment.WallId);
                supportedEndpointByWallId[assessment.WallId] = supportedEndpointCount;
                return ReviewShortIsolatedGraphWallAssessment(
                    assessment,
                    component,
                    supportedEndpointCount);
            })
            .ToArray();

        if (reviewWallIds.Count == 0)
        {
            return;
        }

        var refinedSegments = context.WallEvidenceMap.Segments
            .Select(segment => segment.WallId is not null && reviewWallIds.Contains(segment.WallId)
                ? segment with
                {
                    Category = WallEvidenceCategory.MediumWallBody,
                    Evidence = AppendEvidence(
                        segment.Evidence,
                        new[]
                        {
                            "wall evidence: short isolated graph fragment requires review before exact placement"
                        })
                }
                : segment)
            .ToArray();
        var refinedBands = context.WallEvidenceMap.Bands
            .Select(band => band.WallId is not null && reviewWallIds.Contains(band.WallId)
                ? band with
                {
                    Evidence = AppendEvidence(
                        band.Evidence,
                        new[]
                        {
                            "wall evidence: short isolated graph fragment requires review before exact placement"
                        })
                }
                : band)
            .ToArray();

        context.WallEvidenceMap = context.WallEvidenceMap with
        {
            Segments = refinedSegments,
            Bands = refinedBands,
            WallAssessments = refinedAssessments
        };

        for (var index = 0; index < context.Walls.Count; index++)
        {
            var wall = context.Walls[index];
            if (!reviewWallIds.Contains(wall.Id) || !componentByWallId.TryGetValue(wall.Id, out var component))
            {
                continue;
            }

            var supportedEndpointCount = supportedEndpointByWallId.TryGetValue(wall.Id, out var count) ? count : 0;
            context.Walls[index] = wall with
            {
                Evidence = AppendEvidence(
                    wall.Evidence,
                    new[]
                    {
                        $"wall evidence assessment: review-only short isolated graph fragment in {component.Id}; {supportedEndpointCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} topology-supported endpoint(s)"
                    })
            };
        }

        context.AddDiagnostic(
            "wall_evidence.short_isolated_graph_walls_reviewed",
            DiagnosticSeverity.Info,
            "wall-graph",
            $"{reviewWallIds.Count} short isolated wall graph fragment(s) were blocked from exact placement until reviewed.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: context.Walls
                .Where(wall => reviewWallIds.Contains(wall.Id))
                .SelectMany(wall => wall.SourcePrimitiveIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            properties: new Dictionary<string, string>
            {
                ["reviewWallCount"] = reviewWallIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["componentCount"] = isolatedComponents.Count(component => component.WallIds.Any(reviewWallIds.Contains)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["wallIds"] = string.Join(",", reviewWallIds.OrderBy(id => id, StringComparer.Ordinal).Take(20)),
                ["componentIds"] = string.Join(",", isolatedComponents
                    .Where(component => component.WallIds.Any(reviewWallIds.Contains))
                    .Select(component => component.Id)
                    .Take(20))
            });
    }

    private static bool ShouldReviewShortIsolatedGraphWall(
        WallEvidenceWallAssessment assessment,
        WallSegment wall,
        IReadOnlyList<WallEdge> wallEdges,
        IReadOnlyDictionary<string, int> nodeDegreeById,
        ScannerOptions options,
        out int supportedEndpointCount)
    {
        supportedEndpointCount = CountSupportedTopologyEndpoints(wallEdges, nodeDegreeById);
        if (assessment.RejectedAsNoise
            || assessment.Decision != WallEvidenceDecision.Accept
            || !assessment.PlacementReady
            || wall.DrawingLength > ShortIsolatedGraphWallReviewLength(options)
            || HasWallLayerEvidence(assessment, wall)
            || supportedEndpointCount > 0)
        {
            return false;
        }

        return wall.DetectionKind is WallDetectionKind.ParallelLinePair
            or WallDetectionKind.SingleLine
            or WallDetectionKind.FragmentMerged;
    }

    private static double ShortIsolatedGraphWallReviewLength(ScannerOptions options) =>
        Math.Max(options.MinWallLength * 2.25, options.DefaultWallThickness * 14.0);

    private static bool HasWallLayerEvidence(
        WallEvidenceWallAssessment assessment,
        WallSegment wall)
    {
        return assessment.Evidence.Concat(wall.Evidence).Any(evidence =>
            evidence.Contains("wall or structural source layer", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("source layer category Wall", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("source layer category Structural", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("classified Wall", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("classified Structural", StringComparison.OrdinalIgnoreCase));
    }

    private static WallEvidenceWallAssessment ReviewShortIsolatedGraphWallAssessment(
        WallEvidenceWallAssessment assessment,
        WallGraphComponent component,
        int supportedEndpointCount)
    {
        var evidence = AppendEvidence(
            assessment.Evidence,
            new[]
            {
                $"wall evidence: short isolated graph fragment in {component.Id} has {supportedEndpointCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} topology-supported endpoint(s); review as possible wall/detail before exact placement",
                "wall evidence: blocked from placement-ready output until topology or human review confirms it"
            });

        return assessment with
        {
            Category = WallEvidenceCategory.MediumWallBody,
            PlacementReady = false,
            RequiresReview = true,
            Decision = WallEvidenceDecision.Review,
            ScoreBreakdown = ReviewShortIsolatedGraphWallScore(assessment.ScoreBreakdown, component),
            Evidence = evidence
        };
    }

    private static WallEvidenceScoreBreakdown ReviewShortIsolatedGraphWallScore(
        WallEvidenceScoreBreakdown score,
        WallGraphComponent component)
    {
        var reviewPenalty = Math.Max(score.NoisePenalty, 0.20);
        var negativeScore = RoundScore(Math.Min(1, Math.Max(score.NegativeScore, reviewPenalty + score.FragmentReviewPenalty)));
        var decisionScore = RoundScore(Math.Max(-1, Math.Min(1, score.PositiveScore - negativeScore)));
        var negativeEvidence = AppendEvidence(
            score.NegativeEvidence,
            new[]
            {
                $"short isolated wall graph fragment in {component.Id} needs review before exact placement"
            });

        return score with
        {
            NegativeScore = negativeScore,
            DecisionScore = decisionScore,
            NoisePenalty = RoundScore(reviewPenalty),
            NegativeEvidence = negativeEvidence
        };
    }

    private static void PromoteMainStructuralMediumWallEvidence(
        ScanContext context,
        IReadOnlyList<WallGraphComponent> components,
        IReadOnlyList<WallEdge> graphEdges,
        IReadOnlyList<WallNode> graphNodes,
        IReadOnlyList<WallGraphRepairCandidate> repairCandidates)
    {
        if (context.WallEvidenceMap.WallAssessments.Count == 0 || components.Count == 0)
        {
            return;
        }

        var mainStructuralComponents = components
            .Where(component => component.Kind == WallGraphComponentKind.MainStructural)
            .Where(component => !component.ExcludedFromStructuralTopology)
            .Where(component => component.WallIds.Count > 0)
            .ToArray();
        if (mainStructuralComponents.Length == 0)
        {
            return;
        }

        var componentByWallId = mainStructuralComponents
            .SelectMany(component => component.WallIds.Select(wallId => new { WallId = wallId, Component = component }))
            .GroupBy(item => item.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Component, StringComparer.Ordinal);
        var wallsById = context.Walls.ToDictionary(wall => wall.Id, StringComparer.Ordinal);
        var edgesByWallId = graphEdges
            .GroupBy(edge => edge.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var nodeDegreeById = graphNodes.ToDictionary(node => node.Id, node => node.Degree, StringComparer.Ordinal);
        var topologyImportBlockedWallIds = BuildTopologyImportBlockedWallIds(repairCandidates);
        var promotedWallIds = new HashSet<string>(StringComparer.Ordinal);
        var promotedAssessments = context.WallEvidenceMap.WallAssessments
            .Select(assessment =>
            {
                if (!componentByWallId.TryGetValue(assessment.WallId, out var component)
                    || !wallsById.TryGetValue(assessment.WallId, out var wall)
                    || !edgesByWallId.TryGetValue(assessment.WallId, out var wallEdges))
                {
                    return assessment;
                }

                if (topologyImportBlockedWallIds.Contains(assessment.WallId))
                {
                    return assessment;
                }

                var supportedEndpointCount = CountSupportedTopologyEndpoints(wallEdges, nodeDegreeById);
                if (!IsTrustedMainStructuralMediumWallAssessment(assessment, wall, supportedEndpointCount))
                {
                    return assessment;
                }

                promotedWallIds.Add(assessment.WallId);
                return PromoteMainStructuralMediumWallAssessment(
                    assessment,
                    component,
                    supportedEndpointCount);
            })
            .ToArray();

        if (promotedWallIds.Count == 0)
        {
            return;
        }

        context.WallEvidenceMap = context.WallEvidenceMap with
        {
            WallAssessments = promotedAssessments
        };

        for (var index = 0; index < context.Walls.Count; index++)
        {
            var wall = context.Walls[index];
            if (!promotedWallIds.Contains(wall.Id) || !componentByWallId.TryGetValue(wall.Id, out var component))
            {
                continue;
            }

            context.Walls[index] = wall with
            {
                Evidence = AppendEvidence(
                    wall.Evidence,
                    new[]
                    {
                        $"wall evidence assessment: medium wall body promoted to placement-ready by main structural graph component {component.Id}"
                    })
            };
        }

        context.AddDiagnostic(
            "wall_evidence.main_structural_medium_walls_promoted",
            DiagnosticSeverity.Info,
            "wall-graph",
            $"{promotedWallIds.Count} medium wall body assessment(s) were promoted to placement-ready by main structural graph continuity.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: context.Walls
                .Where(wall => promotedWallIds.Contains(wall.Id))
                .SelectMany(wall => wall.SourcePrimitiveIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            properties: new Dictionary<string, string>
            {
                ["promotedWallCount"] = promotedWallIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["wallIds"] = string.Join(",", promotedWallIds.OrderBy(id => id, StringComparer.Ordinal).Take(20))
            });
    }

    private static IReadOnlySet<string> BuildTopologyImportBlockedWallIds(
        IReadOnlyList<WallGraphRepairCandidate> repairCandidates)
    {
        var wallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in repairCandidates.Where(candidate =>
            candidate.ImportImpact == WallGraphRepairImportImpact.TopologyImportBlocked))
        {
            foreach (var wallId in candidate.WallIds.Where(id => !string.IsNullOrWhiteSpace(id)))
            {
                wallIds.Add(wallId);
            }

            if (!string.IsNullOrWhiteSpace(candidate.HostWallId))
            {
                wallIds.Add(candidate.HostWallId);
            }
        }

        return wallIds;
    }

    private static bool IsTrustedMainStructuralMediumWallAssessment(
        WallEvidenceWallAssessment assessment,
        WallSegment wall,
        int supportedEndpointCount)
    {
        if (assessment.RejectedAsNoise
            || assessment.Decision != WallEvidenceDecision.Review
            || assessment.Category != WallEvidenceCategory.MediumWallBody
            || assessment.PlacementReady
            || assessment.Confidence.Value < 0.82)
        {
            return false;
        }

        if (wall.WallType == WallType.Unknown)
        {
            return false;
        }

        if (wall.DetectionKind != WallDetectionKind.ParallelLinePair
            && !assessment.Evidence.Any(item => item.Contains("parallel wall-face pair", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (wall.FragmentEvidence?.RequiresGeometryReview == true)
        {
            return false;
        }

        if (supportedEndpointCount < 2
            && !IsTrustedOneEndpointMainStructuralMediumWallAssessment(assessment, wall, supportedEndpointCount))
        {
            return false;
        }

        if (assessment.Evidence.Any(IsHardRiskReviewWallEvidence)
            || assessment.Evidence.Any(IsMainStructuralPromotionBlockedEvidence))
        {
            return false;
        }

        return true;
    }

    private static bool IsTrustedOneEndpointMainStructuralMediumWallAssessment(
        WallEvidenceWallAssessment assessment,
        WallSegment wall,
        int supportedEndpointCount)
    {
        if (supportedEndpointCount != 1
            || wall.WallType != WallType.Interior
            || wall.DrawingLength < MinOneEndpointMainStructuralMediumLength)
        {
            return false;
        }

        var evidence = assessment.Evidence
            .Concat(wall.Evidence)
            .ToArray();
        if (evidence.Any(item => item.Contains("weak/fragmented pair evidence", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return TryReadPairScore(evidence, out var pairScore)
            && pairScore >= MinOneEndpointMainStructuralMediumPairScore;
    }

    private static bool TryReadPairScore(IEnumerable<string> evidence, out double pairScore)
    {
        foreach (var item in evidence)
        {
            const string marker = "pair score";
            var index = item.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var valueStart = index + marker.Length;
            while (valueStart < item.Length && char.IsWhiteSpace(item[valueStart]))
            {
                valueStart++;
            }

            var valueEnd = valueStart;
            while (valueEnd < item.Length
                && (char.IsDigit(item[valueEnd]) || item[valueEnd] == '.' || item[valueEnd] == ','))
            {
                valueEnd++;
            }

            if (valueEnd == valueStart)
            {
                continue;
            }

            var value = item[valueStart..valueEnd].Replace(',', '.');
            if (double.TryParse(
                    value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out pairScore))
            {
                return true;
            }
        }

        pairScore = 0;
        return false;
    }

    private static bool IsMainStructuralPromotionBlockedEvidence(string evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return false;
        }

        return evidence.Contains("duplicate wall-face", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("already represented", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("block exact placement", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("review before exact placement", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("until reviewed", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("one structurally supported endpoint", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("one trusted structural endpoint", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("fewer than two distinct structural wall connections", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("topology import", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("repair candidate", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("endpoint-to-wall snap", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("fragment geometry requires review", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("unknown fragment-merged", StringComparison.OrdinalIgnoreCase);
    }

    private static void PromoteSecondaryInteriorFragmentWallEvidence(
        ScanContext context,
        IReadOnlyList<WallGraphComponent> components)
    {
        if (context.WallEvidenceMap.WallAssessments.Count == 0 || components.Count == 0)
        {
            return;
        }

        var secondaryFragmentComponents = components
            .Where(component => component.Kind == WallGraphComponentKind.SecondaryStructural)
            .Where(component => !component.ExcludedFromStructuralTopology)
            .Where(component => component.WallIds.Count == 1 && component.EdgeIds.Count > 0)
            .ToArray();
        if (secondaryFragmentComponents.Length == 0)
        {
            return;
        }

        var componentByWallId = secondaryFragmentComponents
            .SelectMany(component => component.WallIds.Select(wallId => new { WallId = wallId, Component = component }))
            .GroupBy(item => item.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Component, StringComparer.Ordinal);
        var wallsById = context.Walls.ToDictionary(wall => wall.Id, StringComparer.Ordinal);
        var promotedWallIds = new HashSet<string>(StringComparer.Ordinal);
        var promotedAssessments = context.WallEvidenceMap.WallAssessments
            .Select(assessment =>
            {
                if (!componentByWallId.TryGetValue(assessment.WallId, out var component)
                    || !wallsById.TryGetValue(assessment.WallId, out var wall)
                    || !IsTrustedSecondaryInteriorFragmentAssessment(assessment, wall, component, context.Options))
                {
                    return assessment;
                }

                promotedWallIds.Add(assessment.WallId);
                return PromoteSecondaryInteriorFragmentAssessment(assessment, component);
            })
            .ToArray();

        if (promotedWallIds.Count == 0)
        {
            return;
        }

        context.WallEvidenceMap = context.WallEvidenceMap with
        {
            WallAssessments = promotedAssessments
        };

        for (var index = 0; index < context.Walls.Count; index++)
        {
            var wall = context.Walls[index];
            if (!promotedWallIds.Contains(wall.Id) || !componentByWallId.TryGetValue(wall.Id, out var component))
            {
                continue;
            }

            context.Walls[index] = wall with
            {
                Evidence = AppendEvidence(
                    wall.Evidence,
                    new[]
                    {
                        $"wall evidence assessment: long interior fragment promoted to placement-ready by secondary structural graph component {component.Id}"
                    })
            };
        }

        context.AddDiagnostic(
            "wall_evidence.secondary_interior_fragments_promoted",
            DiagnosticSeverity.Info,
            "wall-graph",
            $"{promotedWallIds.Count} long interior fragment wall assessment(s) were promoted to placement-ready by secondary structural graph context.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: context.Walls
                .Where(wall => promotedWallIds.Contains(wall.Id))
                .SelectMany(wall => wall.SourcePrimitiveIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            properties: new Dictionary<string, string>
            {
                ["promotedWallCount"] = promotedWallIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["wallIds"] = string.Join(",", promotedWallIds.OrderBy(id => id, StringComparer.Ordinal).Take(20))
            });
    }

    private static bool IsTrustedSecondaryInteriorFragmentAssessment(
        WallEvidenceWallAssessment assessment,
        WallSegment wall,
        WallGraphComponent component,
        ScannerOptions options)
    {
        if (assessment.RejectedAsNoise
            || assessment.Decision != WallEvidenceDecision.Review
            || assessment.Category != WallEvidenceCategory.MediumWallBody
            || assessment.PlacementReady
            || assessment.Confidence.Value < 0.64)
        {
            return false;
        }

        if (component.Kind != WallGraphComponentKind.SecondaryStructural
            || component.ExcludedFromStructuralTopology
            || component.WallIds.Count != 1
            || component.EdgeIds.Count == 0)
        {
            return false;
        }

        if (wall.WallType != WallType.Interior
            || wall.DetectionKind != WallDetectionKind.FragmentMerged
            || wall.DrawingLength <= RecoverableInteriorFragmentLength(options)
            || !HasSafeSecondaryInteriorFragmentGeometry(wall, options))
        {
            return false;
        }

        if (!assessment.Evidence.Any(item => item.Contains("unlayered fragment-merged wall candidate", StringComparison.OrdinalIgnoreCase))
            || !assessment.Evidence.Any(item => item.Contains("only one trusted structural endpoint", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (assessment.Evidence.Any(IsHardRiskReviewWallEvidence)
            || assessment.Evidence.Any(IsSecondaryInteriorFragmentPromotionBlockedEvidence))
        {
            return false;
        }

        return true;
    }

    private static bool HasSafeSecondaryInteriorFragmentGeometry(WallSegment wall, ScannerOptions options)
    {
        if (wall.FragmentEvidence is not { RequiresGeometryReview: false } fragmentEvidence)
        {
            return false;
        }

        var uniqueSourcePrimitiveCount = Math.Max(0, wall.SourcePrimitiveIds.Count - fragmentEvidence.DuplicatePrimitiveCount);
        var fragmentCount = Math.Max(fragmentEvidence.FragmentCount, uniqueSourcePrimitiveCount);
        var maxHealedGap = Math.Max(options.DefaultWallThickness * 2.0, options.WallSnapTolerance * 4.0);
        return fragmentCount is >= 2 and <= MaxSecondaryInteriorFragmentPromotionFragments
            && fragmentEvidence.DuplicatePrimitiveCount <= MaxSecondaryInteriorFragmentPromotionDuplicatePrimitives
            && fragmentEvidence.GapRatio <= MaxSecondaryInteriorFragmentPromotionGapRatio
            && fragmentEvidence.TotalHealedGap <= maxHealedGap;
    }

    private static bool IsSecondaryInteriorFragmentPromotionBlockedEvidence(string evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return false;
        }

        if (IsOneEndpointFragmentReviewEvidence(evidence))
        {
            return false;
        }

        return evidence.Contains("duplicate wall-face", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("already represented", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("block exact placement", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("review before exact placement", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("until reviewed", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("topology import", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("repair candidate", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("endpoint-to-wall snap", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("fragment geometry requires review", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("unknown fragment-merged", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("no trusted structural endpoint", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOneEndpointFragmentReviewEvidence(string evidence) =>
        evidence.Contains("unlayered fragment-merged wall candidate", StringComparison.OrdinalIgnoreCase)
        && evidence.Contains("only one trusted structural endpoint", StringComparison.OrdinalIgnoreCase);

    private static int CountSupportedTopologyEndpoints(
        IReadOnlyList<WallEdge> wallEdges,
        IReadOnlyDictionary<string, int> nodeDegreeById)
    {
        var supportedNodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in wallEdges)
        {
            Count(edge.FromNodeId);
            Count(edge.ToNodeId);
        }

        return supportedNodeIds.Count;

        void Count(string nodeId)
        {
            if (nodeDegreeById.TryGetValue(nodeId, out var degree) && degree > 1)
            {
                supportedNodeIds.Add(nodeId);
            }
        }
    }

    private static WallEvidenceWallAssessment PromoteMainStructuralMediumWallAssessment(
        WallEvidenceWallAssessment assessment,
        WallGraphComponent component,
        int supportedEndpointCount)
    {
        var evidence = AppendEvidence(
            assessment.Evidence,
            new[]
            {
                $"wall evidence: promoted to placement-ready by main structural graph component {component.Id}",
                $"wall evidence: {supportedEndpointCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} topology-supported endpoint(s) in main structural component"
            });
        return assessment with
        {
            PlacementReady = true,
            RequiresReview = false,
            Decision = WallEvidenceDecision.Accept,
            ScoreBreakdown = PromoteMainStructuralMediumWallScore(assessment.ScoreBreakdown),
            Evidence = evidence
        };
    }

    private static WallEvidenceWallAssessment PromoteSecondaryInteriorFragmentAssessment(
        WallEvidenceWallAssessment assessment,
        WallGraphComponent component)
    {
        var evidence = AppendEvidence(
            assessment.Evidence,
            new[]
            {
                $"wall evidence: promoted to placement-ready by secondary structural graph component {component.Id}",
                "wall evidence: long interior fragment has one trusted structural endpoint and is near the main wall body"
            });
        return assessment with
        {
            PlacementReady = true,
            RequiresReview = false,
            Decision = WallEvidenceDecision.Accept,
            ScoreBreakdown = PromoteSecondaryInteriorFragmentScore(assessment.ScoreBreakdown),
            Evidence = evidence
        };
    }

    private static WallEvidenceScoreBreakdown PromoteMainStructuralMediumWallScore(
        WallEvidenceScoreBreakdown score)
    {
        var structuralScore = RoundScore(Math.Max(score.StructuralSupportScore, 0.20));
        var positiveScore = RoundScore(Math.Min(1, Math.Max(score.PositiveScore, score.PairSupportScore + score.LayerSupportScore + structuralScore + score.RecoverySupportScore)));
        var negativeEvidence = score.NegativeEvidence
            .Where(item => !item.Contains("not placement-ready", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var decisionScore = RoundScore(Math.Max(-1, Math.Min(1, positiveScore - score.NegativeScore)));
        var positiveEvidence = AppendEvidence(
            score.PositiveEvidence,
            new[]
            {
                "main structural graph continuity"
            });

        return new WallEvidenceScoreBreakdown(
            positiveScore,
            score.NegativeScore,
            decisionScore,
            score.PairSupportScore,
            score.LayerSupportScore,
            structuralScore,
            score.RecoverySupportScore,
            score.NoisePenalty,
            score.FragmentReviewPenalty,
            positiveEvidence,
            negativeEvidence);
    }

    private static WallEvidenceScoreBreakdown PromoteSecondaryInteriorFragmentScore(
        WallEvidenceScoreBreakdown score)
    {
        var structuralScore = RoundScore(Math.Max(score.StructuralSupportScore, 0.16));
        var recoveryScore = RoundScore(Math.Max(score.RecoverySupportScore, 0.12));
        var positiveScore = RoundScore(Math.Min(1, Math.Max(score.PositiveScore, score.PairSupportScore + score.LayerSupportScore + structuralScore + recoveryScore)));
        var negativeEvidence = score.NegativeEvidence
            .Where(item => !item.Contains("not placement-ready", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var decisionScore = RoundScore(Math.Max(-1, Math.Min(1, positiveScore - score.NegativeScore)));
        var positiveEvidence = AppendEvidence(
            score.PositiveEvidence,
            new[]
            {
                "secondary structural interior fragment continuity"
            });

        return new WallEvidenceScoreBreakdown(
            positiveScore,
            score.NegativeScore,
            decisionScore,
            score.PairSupportScore,
            score.LayerSupportScore,
            structuralScore,
            recoveryScore,
            score.NoisePenalty,
            score.FragmentReviewPenalty,
            positiveEvidence,
            negativeEvidence);
    }

    private void SynchronizeWallEvidenceGeometry(
        ScanContext context,
        IReadOnlyDictionary<string, WallSegment> normalizedWallsById)
    {
        if (normalizedWallsById.Count == 0
            || context.WallEvidenceMap.Segments.Count == 0
            && context.WallEvidenceMap.Bands.Count == 0
            && context.WallEvidenceMap.WallAssessments.Count == 0)
        {
            return;
        }

        const string evidenceMessage = "wall evidence geometry synchronized after wall graph topology normalization";
        var synchronizedWallIds = new HashSet<string>(StringComparer.Ordinal);
        var synchronizedSegmentCount = 0;
        var synchronizedBandCount = 0;
        var synchronizedAssessmentCount = 0;

        var segments = context.WallEvidenceMap.Segments
            .Select(segment =>
            {
                if (segment.WallId is null
                    || !normalizedWallsById.TryGetValue(segment.WallId, out var normalizedWall)
                    || segment.Line.Equals(normalizedWall.CenterLine)
                    && segment.Bounds.Equals(normalizedWall.Bounds))
                {
                    return segment;
                }

                synchronizedSegmentCount++;
                synchronizedWallIds.Add(segment.WallId);
                return segment with
                {
                    Line = normalizedWall.CenterLine,
                    Bounds = normalizedWall.Bounds,
                    Evidence = AppendEvidence(segment.Evidence, new[] { evidenceMessage })
                };
            })
            .ToArray();

        var bands = context.WallEvidenceMap.Bands
            .Select(band =>
            {
                if (band.WallId is null
                    || !normalizedWallsById.TryGetValue(band.WallId, out var normalizedWall)
                    || band.CenterLine.Equals(normalizedWall.CenterLine))
                {
                    return band;
                }

                synchronizedBandCount++;
                synchronizedWallIds.Add(band.WallId);
                return band with
                {
                    CenterLine = normalizedWall.CenterLine,
                    Evidence = AppendEvidence(band.Evidence, new[] { evidenceMessage })
                };
            })
            .ToArray();

        var assessments = context.WallEvidenceMap.WallAssessments
            .Select(assessment =>
            {
                if (!normalizedWallsById.TryGetValue(assessment.WallId, out var normalizedWall)
                    || assessment.Bounds.Equals(normalizedWall.Bounds))
                {
                    return assessment;
                }

                synchronizedAssessmentCount++;
                synchronizedWallIds.Add(assessment.WallId);
                return assessment with
                {
                    Bounds = normalizedWall.Bounds,
                    Evidence = AppendEvidence(assessment.Evidence, new[] { evidenceMessage })
                };
            })
            .ToArray();

        if (synchronizedWallIds.Count == 0)
        {
            return;
        }

        context.WallEvidenceMap = context.WallEvidenceMap with
        {
            Segments = segments,
            Bands = bands,
            WallAssessments = assessments
        };

        context.AddDiagnostic(
            "wall_evidence.geometry_synchronized",
            DiagnosticSeverity.Info,
            Name,
            "Wall Evidence V2 geometry was synchronized with topology-normalized wall coordinates.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: context.Walls
                .Where(wall => synchronizedWallIds.Contains(wall.Id))
                .SelectMany(wall => wall.SourcePrimitiveIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            properties: new Dictionary<string, string>
            {
                ["synchronizedWallCount"] = synchronizedWallIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["synchronizedSegmentCount"] = synchronizedSegmentCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["synchronizedBandCount"] = synchronizedBandCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["synchronizedAssessmentCount"] = synchronizedAssessmentCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["wallIds"] = string.Join(",", synchronizedWallIds.OrderBy(id => id, StringComparer.Ordinal).Take(20))
            });
    }

    private static WallEvidenceWallAssessment RefineObjectLikeComponentAssessment(
        WallEvidenceWallAssessment assessment,
        WallGraphComponent component)
    {
        var evidence = AppendEvidence(
            assessment.Evidence,
            new[]
            {
                $"wall evidence: reclassified as object/fixture detail because graph component {component.Id} is {component.Kind}",
                "wall evidence: component excluded from structural topology as compact object-like linework"
            });
        return assessment with
        {
            Category = WallEvidenceCategory.ObjectOrFixtureDetail,
            Confidence = new Confidence(Math.Max(assessment.Confidence.Value, 0.74)),
            PlacementReady = false,
            RequiresReview = true,
            RejectedAsNoise = true,
            Decision = WallEvidenceDecision.Reject,
            ScoreBreakdown = PenalizeObjectLikeComponentEvidence(assessment.ScoreBreakdown, component),
            Evidence = evidence
        };
    }

    private static WallEvidenceScoreBreakdown PenalizeObjectLikeComponentEvidence(
        WallEvidenceScoreBreakdown score,
        WallGraphComponent component)
    {
        var noisePenalty = Math.Max(score.NoisePenalty, 0.75);
        var negativeScore = RoundScore(Math.Min(1, Math.Max(score.NegativeScore, noisePenalty + score.FragmentReviewPenalty)));
        var decisionScore = RoundScore(Math.Max(-1, Math.Min(1, score.PositiveScore - negativeScore)));
        var negativeEvidence = AppendEvidence(
            score.NegativeEvidence,
            new[]
            {
                $"explicit non-wall evidence: {WallEvidenceCategory.ObjectOrFixtureDetail}",
                $"wall graph component {component.Id} is {component.Kind} and excluded from structural topology"
            });

        return new WallEvidenceScoreBreakdown(
            score.PositiveScore,
            negativeScore,
            decisionScore,
            score.PairSupportScore,
            score.LayerSupportScore,
            score.StructuralSupportScore,
            score.RecoverySupportScore,
            RoundScore(noisePenalty),
            score.FragmentReviewPenalty,
            score.PositiveEvidence,
            negativeEvidence);
    }

    private static double RoundScore(double value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static IReadOnlyList<string> AppendEvidence(
        IReadOnlyList<string> existing,
        IEnumerable<string> additions) =>
        existing
            .Concat(additions)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static RawWallGraphComponent CreateRawComponent(
        int pageNumber,
        IEnumerable<string> wallIds,
        IEnumerable<string> nodeIds,
        IEnumerable<string> edgeIds,
        IReadOnlyDictionary<string, WallSegment> wallsById,
        IReadOnlyDictionary<string, WallNode> nodesById,
        ScannerOptions options)
    {
        var componentWallIds = wallIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && wallsById.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var componentNodeIds = nodeIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && nodesById.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var componentEdgeIds = edgeIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var componentWalls = componentWallIds.Select(id => wallsById[id]).ToArray();
        var componentNodes = componentNodeIds.Select(id => nodesById[id]).ToArray();
        var wallBounds = componentWalls.Select(wall => wall.Bounds);
        var nodeBounds = componentNodes.Select(node => PlanRect.FromPoints(node.Position, node.Position));
        var bounds = PlanRect.Union(wallBounds.Concat(nodeBounds));
        var drawingLength = componentWalls.Sum(wall => wall.DrawingLength);
        var sourcePrimitiveIds = componentWalls
            .SelectMany(wall => wall.SourcePrimitiveIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var orientationTolerance = Math.Max(1.5, options.WallSnapTolerance);
        var horizontalWallCount = componentWalls.Count(wall => wall.CenterLine.IsHorizontal(orientationTolerance));
        var verticalWallCount = componentWalls.Count(wall => wall.CenterLine.IsVertical(orientationTolerance));
        var diagonalWallCount = componentWalls.Length - horizontalWallCount - verticalWallCount;
        var shortDetailWallLength = ShortDetailComponentWallLength(options);
        var shortWallCount = componentWalls.Count(wall => wall.DrawingLength <= shortDetailWallLength);
        var singleLineWallCount = componentWalls.Count(wall => wall.DetectionKind == WallDetectionKind.SingleLine);
        var pairedWallCount = componentWalls.Count(wall => wall.DetectionKind == WallDetectionKind.ParallelLinePair);
        var fragmentMergedWallCount = componentWalls.Count(wall => wall.DetectionKind == WallDetectionKind.FragmentMerged);
        var interiorWallCount = componentWalls.Count(wall => wall.WallType == WallType.Interior);
        var exteriorWallCount = componentWalls.Count(wall => wall.WallType == WallType.Exterior);

        return new RawWallGraphComponent(
            pageNumber,
            componentWallIds,
            componentNodeIds,
            componentEdgeIds,
            sourcePrimitiveIds,
            bounds,
            drawingLength,
            horizontalWallCount,
            verticalWallCount,
            diagonalWallCount,
            shortWallCount,
            singleLineWallCount,
            pairedWallCount,
            fragmentMergedWallCount,
            interiorWallCount,
            exteriorWallCount);
    }

    private static WallGraphComponentKind ClassifyComponent(
        RawWallGraphComponent component,
        RawWallGraphComponent? mainComponent,
        PlanRect mainBounds,
        ScanContext context)
    {
        if (mainComponent is not null
            && ReferenceEquals(component, mainComponent)
            && (component.WallIds.Count >= 3 || component.EdgeIds.Count >= 3))
        {
            return WallGraphComponentKind.MainStructural;
        }

        if (LooksLikeCompactStructuralPairedWallCluster(component, mainComponent, context))
        {
            return WallGraphComponentKind.SecondaryStructural;
        }

        if (LooksLikeAnchoredSinglePairedWallBody(component, mainComponent, context))
        {
            return WallGraphComponentKind.SecondaryStructural;
        }

        if (LooksLikeObjectIsland(component, mainBounds)
            || LooksLikeDenseDetailOrStairIsland(component, mainBounds, context.Options))
        {
            return WallGraphComponentKind.ObjectLikeIsland;
        }

        if (LooksLikeRecoverableInteriorFragment(component, mainComponent, context.Options))
        {
            return WallGraphComponentKind.SecondaryStructural;
        }

        if (component.WallIds.Count <= 1 || component.EdgeIds.Count == 0 || component.NodeIds.Count <= 2)
        {
            return WallGraphComponentKind.IsolatedFragment;
        }

        return WallGraphComponentKind.SecondaryStructural;
    }

    private static bool LooksLikeCompactStructuralPairedWallCluster(
        RawWallGraphComponent component,
        RawWallGraphComponent? mainComponent,
        ScanContext context)
    {
        if (mainComponent is null
            || ReferenceEquals(component, mainComponent)
            || component.Bounds.IsEmpty
            || mainComponent.Bounds.IsEmpty
            || component.WallIds.Count < 2
            || component.WallIds.Count > 6
            || component.EdgeIds.Count == 0
            || component.DiagonalWallCount > 0
            || component.PairedWallCount < 2
            || component.DrawingLength < CompactStructuralPairedWallClusterLength(context.Options))
        {
            return false;
        }

        var structuralNeighborhood = mainComponent.Bounds.Inflate(Math.Max(
            UnresolvedEndpointGapReviewTolerance(context.Options) * 2.0,
            context.Options.DefaultWallThickness * 10.0));
        if (!structuralNeighborhood.Intersects(component.Bounds))
        {
            return false;
        }

        var wallsById = context.Walls.ToDictionary(wall => wall.Id, StringComparer.Ordinal);
        var assessmentsByWallId = context.WallEvidenceMap.WallAssessments
            .Where(assessment => !string.IsNullOrWhiteSpace(assessment.WallId))
            .GroupBy(assessment => assessment.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        var trustedPairedWallCount = 0;
        var supportedPairedReturnWallCount = 0;
        var hasEndpointSupportedWall = false;
        foreach (var wallId in component.WallIds)
        {
            if (!wallsById.TryGetValue(wallId, out var wall)
                || !assessmentsByWallId.TryGetValue(wallId, out var assessment))
            {
                continue;
            }

            if (IsTrustedCompactStructuralPairedWall(assessment, wall, context.Options))
            {
                trustedPairedWallCount++;
            }

            if (IsSupportedCompactStructuralPairedReturnWall(assessment, wall, context.Options))
            {
                supportedPairedReturnWallCount++;
                hasEndpointSupportedWall |= HasStructuralEndpointSupportEvidence(assessment);
            }
        }

        if (trustedPairedWallCount >= 2)
        {
            return true;
        }

        return trustedPairedWallCount >= 1
            && supportedPairedReturnWallCount == component.WallIds.Count
            && hasEndpointSupportedWall
            && component.DrawingLength >= CompactStructuralPairedReturnClusterLength(context.Options);
    }

    private static bool IsTrustedCompactStructuralPairedWall(
        WallEvidenceWallAssessment assessment,
        WallSegment wall,
        ScannerOptions options)
    {
        if (assessment.RejectedAsNoise
            || assessment.Category != WallEvidenceCategory.StrongWallBody
            || assessment.Confidence.Value < 0.86
            || (!assessment.PlacementReady && assessment.Decision != WallEvidenceDecision.Accept)
            || wall.DrawingLength < options.MinWallLength
            || wall.FragmentEvidence?.RequiresGeometryReview == true
            || HasOpeningKeywordEvidence(wall))
        {
            return false;
        }

        var evidence = assessment.Evidence
            .Concat(wall.Evidence)
            .ToArray();
        if (evidence.Any(IsHardRiskReviewWallEvidence)
            || evidence.Any(IsMainStructuralPromotionBlockedEvidence)
            || evidence.Any(item => item.Contains("weak/fragmented pair evidence", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (wall.DetectionKind != WallDetectionKind.ParallelLinePair
            && !evidence.Any(item => item.Contains("parallel wall-face pair", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !TryReadPairScore(evidence, out var pairScore)
            || pairScore >= MinCompactStructuralPairedWallPairScore;
    }

    private static bool IsSupportedCompactStructuralPairedReturnWall(
        WallEvidenceWallAssessment assessment,
        WallSegment wall,
        ScannerOptions options)
    {
        if (assessment.RejectedAsNoise
            || assessment.Category != WallEvidenceCategory.StrongWallBody
            || assessment.Confidence.Value < 0.80
            || (!assessment.PlacementReady && assessment.Decision != WallEvidenceDecision.Accept)
            || wall.DrawingLength < options.MinWallLength
            || wall.FragmentEvidence?.RequiresGeometryReview == true
            || HasOpeningKeywordEvidence(wall))
        {
            return false;
        }

        var evidence = assessment.Evidence
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(wall.Evidence)
            .ToArray();
        if (evidence.Any(IsHardRiskReviewWallEvidence)
            || evidence.Any(IsMainStructuralPromotionBlockedEvidence)
            || evidence.Any(item => item.Contains("weak/fragmented pair evidence", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (wall.DetectionKind != WallDetectionKind.ParallelLinePair
            && !evidence.Any(item => item.Contains("parallel wall-face pair", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !TryReadPairScore(evidence, out var pairScore)
            || pairScore >= 0.60;
    }

    private static bool HasStructuralEndpointSupportEvidence(WallEvidenceWallAssessment assessment) =>
        assessment.ScoreBreakdown.PositiveEvidence
            .Concat(assessment.Evidence)
            .Any(item =>
                item.Contains("endpoint supported by structural context", StringComparison.OrdinalIgnoreCase)
                || item.Contains("endpoints supported by structural context", StringComparison.OrdinalIgnoreCase)
                || item.Contains("structural graph support", StringComparison.OrdinalIgnoreCase));

    private static double CompactStructuralPairedWallClusterLength(ScannerOptions options) =>
        Math.Max(options.MinWallLength * 5.0, options.DefaultWallThickness * 30.0);

    private static double CompactStructuralPairedReturnClusterLength(ScannerOptions options) =>
        Math.Max(options.MinWallLength * 4.0, options.DefaultWallThickness * 24.0);

    private static bool LooksLikeAnchoredSinglePairedWallBody(
        RawWallGraphComponent component,
        RawWallGraphComponent? mainComponent,
        ScanContext context)
    {
        if (mainComponent is null
            || ReferenceEquals(component, mainComponent)
            || component.Bounds.IsEmpty
            || mainComponent.Bounds.IsEmpty
            || component.WallIds.Count != 1
            || component.EdgeIds.Count == 0
            || component.NodeIds.Count == 0
            || component.PairedWallCount != 1
            || component.DiagonalWallCount > 0
            || component.DrawingLength < AnchoredSinglePairedWallBodyLength(context.Options))
        {
            return false;
        }

        var structuralNeighborhood = mainComponent.Bounds.Inflate(UnresolvedEndpointGapReviewTolerance(context.Options));
        if (!structuralNeighborhood.Intersects(component.Bounds))
        {
            return false;
        }

        var wallsById = context.Walls.ToDictionary(wall => wall.Id, StringComparer.Ordinal);
        var assessmentsByWallId = context.WallEvidenceMap.WallAssessments
            .Where(assessment => !string.IsNullOrWhiteSpace(assessment.WallId))
            .GroupBy(assessment => assessment.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var wallId = component.WallIds[0];
        if (!wallsById.TryGetValue(wallId, out var wall)
            || !assessmentsByWallId.TryGetValue(wallId, out var assessment)
            || !IsTrustedCompactStructuralPairedWall(assessment, wall, context.Options))
        {
            return false;
        }

        if (wall.WallType == WallType.Unknown && !HasWallLayerEvidence(assessment, wall))
        {
            return false;
        }

        return HasEndpointAttachmentToMainComponent(wall, mainComponent, wallsById, context.Options);
    }

    private static double AnchoredSinglePairedWallBodyLength(ScannerOptions options) =>
        Math.Max(options.MinWallLength * 3.0, options.DefaultWallThickness * 18.0);

    private static bool HasEndpointAttachmentToMainComponent(
        WallSegment wall,
        RawWallGraphComponent mainComponent,
        IReadOnlyDictionary<string, WallSegment> wallsById,
        ScannerOptions options)
    {
        var attachmentTolerance = UnresolvedEndpointGapReviewTolerance(options);
        foreach (var hostWallId in mainComponent.WallIds)
        {
            if (!wallsById.TryGetValue(hostWallId, out var hostWall)
                || string.Equals(hostWall.Id, wall.Id, StringComparison.Ordinal)
                || hostWall.PageNumber != wall.PageNumber)
            {
                continue;
            }

            if (EndpointAttachesToWall(wall.CenterLine.Start, hostWall.CenterLine, attachmentTolerance)
                || EndpointAttachesToWall(wall.CenterLine.End, hostWall.CenterLine, attachmentTolerance))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EndpointAttachesToWall(
        PlanPoint endpoint,
        PlanLineSegment hostLine,
        double tolerance)
    {
        if (endpoint.DistanceTo(hostLine.Start) <= tolerance
            || endpoint.DistanceTo(hostLine.End) <= tolerance)
        {
            return true;
        }

        var parameterTolerance = tolerance / Math.Max(hostLine.Length, 1.0);
        var parameter = hostLine.ProjectParameter(endpoint);
        return parameter >= -parameterTolerance
            && parameter <= 1.0 + parameterTolerance
            && hostLine.DistanceToPoint(endpoint) <= tolerance;
    }

    private static string? StructuralTopologyExclusionReason(
        RawWallGraphComponent component,
        WallGraphComponentKind kind,
        RawWallGraphComponent? mainComponent,
        ScanContext context)
    {
        if (kind == WallGraphComponentKind.ObjectLikeIsland
            && context.Options.ExcludeObjectLikeWallComponentsFromStructuralTopology)
        {
            if (LooksLikeDenseDetailOrStairIsland(component, mainComponent?.Bounds ?? PlanRect.Empty, context.Options))
            {
                return "compact dense stair/detail-like linework";
            }

            return "compact disconnected object-like linework";
        }

        if (kind == WallGraphComponentKind.IsolatedFragment
            && context.Options.ExcludeWeakWallFragmentsFromStructuralTopology
            && IsDetachedWeakWallFragment(component, mainComponent, context.Options)
            && !HasNearbyOpeningEvidence(component, context)
            && !OverlapsSurfacePatternRequiringReview(component, context))
        {
            return "isolated wall fragment with weak topology";
        }

        return null;
    }

    private static bool IsDetachedWeakWallFragment(
        RawWallGraphComponent component,
        RawWallGraphComponent? mainComponent,
        ScannerOptions options)
    {
        if (mainComponent is null
            || ReferenceEquals(component, mainComponent)
            || component.Bounds.IsEmpty
            || mainComponent.Bounds.IsEmpty)
        {
            return false;
        }

        var structuralNeighborhood = mainComponent.Bounds.Inflate(UnresolvedEndpointGapReviewTolerance(options));
        return !structuralNeighborhood.Intersects(component.Bounds);
    }

    private static bool HasNearbyOpeningEvidence(
        RawWallGraphComponent component,
        ScanContext context)
    {
        var page = context.Document.Pages.FirstOrDefault(page => page.Number == component.PageNumber);
        if (page is null)
        {
            return false;
        }

        var searchBounds = component.Bounds.Inflate(Math.Max(
            context.Options.MaxOpeningGap * 1.5,
            context.Options.WallSnapTolerance * 8.0));

        return page.Primitives
            .Where(primitive => !component.SourcePrimitiveIds.Contains(primitive.SourceId ?? string.Empty, StringComparer.Ordinal))
            .Where(primitive => primitive.Bounds.Intersects(searchBounds))
            .Any(IsOpeningEvidencePrimitive);
    }

    private static bool OverlapsSurfacePatternRequiringReview(
        RawWallGraphComponent component,
        ScanContext context)
    {
        var tolerance = Math.Max(context.Options.WallSnapTolerance * 3.0, context.Options.DefaultWallThickness * 2.0);
        var componentBounds = component.Bounds.Inflate(tolerance);
        return context.SurfacePatterns
            .Where(pattern => pattern.PageNumber == component.PageNumber)
            .Where(pattern => pattern.RequiresReview || pattern.ExcludedFromStructuralTopology)
            .Any(pattern => pattern.Bounds.Intersects(componentBounds));
    }

    private static bool IsOpeningEvidencePrimitive(PlanPrimitive primitive)
    {
        if (primitive is not ArcPrimitive && primitive is not SymbolPrimitive)
        {
            return false;
        }

        return ContainsOpeningKeyword(primitive.Layer)
            || ContainsOpeningKeyword(primitive.Source.Layer)
            || ContainsOpeningKeyword(primitive.Source.SourceId)
            || ContainsOpeningKeyword(primitive.Source.BlockName)
            || (primitive is SymbolPrimitive symbol && ContainsOpeningKeyword(symbol.Name));
    }

    private static bool ContainsOpeningKeyword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("DOOR", StringComparison.OrdinalIgnoreCase)
            || value.Contains("OPENING", StringComparison.OrdinalIgnoreCase)
            || value.Contains("WINDOW", StringComparison.OrdinalIgnoreCase)
            || value.Contains("WIND", StringComparison.OrdinalIgnoreCase)
            || value.Contains("VINDU", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeObjectIsland(RawWallGraphComponent component, PlanRect mainBounds)
    {
        if (component.Bounds.IsEmpty
            || mainBounds.IsEmpty
            || component.WallIds.Count < 2
            || component.WallIds.Count > 12
            || component.NodeIds.Count > 16)
        {
            return false;
        }

        var componentWidth = Math.Max(component.Bounds.Width, 0);
        var componentHeight = Math.Max(component.Bounds.Height, 0);
        var componentExtent = Math.Max(componentWidth, componentHeight);
        var mainShortSide = Math.Min(mainBounds.Width, mainBounds.Height);
        var mainArea = Math.Max(1, mainBounds.Area);
        var componentAreaRatio = component.Bounds.Area / mainArea;
        var compactExtentLimit = Math.Max(24, mainShortSide * 0.40);

        return componentExtent <= compactExtentLimit
            && componentAreaRatio <= 0.04;
    }

    private static bool LooksLikeDenseDetailOrStairIsland(
        RawWallGraphComponent component,
        PlanRect mainBounds,
        ScannerOptions options)
    {
        if (!options.FilterCompactObjectLineworkFromWalls
            || component.Bounds.IsEmpty
            || mainBounds.IsEmpty
            || component.WallIds.Count < 8
            || component.EdgeIds.Count < 8
            || component.NodeIds.Count < 8)
        {
            return false;
        }

        var componentWidth = Math.Max(component.Bounds.Width, 0);
        var componentHeight = Math.Max(component.Bounds.Height, 0);
        var componentShortSide = Math.Min(componentWidth, componentHeight);
        var componentExtent = Math.Max(componentWidth, componentHeight);
        if (componentShortSide <= 0.001 || componentExtent <= 0.001)
        {
            return false;
        }

        var mainShortSide = Math.Max(Math.Min(mainBounds.Width, mainBounds.Height), 1);
        var componentArea = Math.Max(1, componentWidth * componentHeight);
        var mainArea = Math.Max(1, mainBounds.Width * mainBounds.Height);
        var componentAreaRatio = componentArea / mainArea;
        var lineDensity = component.DrawingLength / componentArea;
        var aspectRatio = componentExtent / componentShortSide;
        var diagonalRatio = component.DiagonalWallCount / (double)component.WallIds.Count;
        var shortWallRatio = component.ShortWallCount / (double)component.WallIds.Count;
        var compactEnough = componentAreaRatio <= 0.055
            && componentExtent <= Math.Max(options.MinWallLength * 4.0, mainShortSide * 0.55);
        if (!compactEnough)
        {
            return false;
        }

        var stairLike = aspectRatio >= 2.0
            && component.DiagonalWallCount >= 3
            && diagonalRatio >= 0.18
            && shortWallRatio >= 0.35
            && lineDensity >= 0.045;
        var denseDetailLike = component.WallIds.Count >= 12
            && shortWallRatio >= 0.6
            && lineDensity >= 0.07
            && componentAreaRatio <= 0.04;

        return stairLike || denseDetailLike;
    }

    private static double ShortDetailComponentWallLength(ScannerOptions options) =>
        Math.Max(options.MinWallLength * 2.75, options.DefaultWallThickness * 16.0);

    private static bool LooksLikeRecoverableInteriorFragment(
        RawWallGraphComponent component,
        RawWallGraphComponent? mainComponent,
        ScannerOptions options)
    {
        if (mainComponent is null
            || ReferenceEquals(component, mainComponent)
            || component.Bounds.IsEmpty
            || mainComponent.Bounds.IsEmpty
            || component.WallIds.Count != 1
            || component.EdgeIds.Count == 0
            || component.FragmentMergedWallCount != 1
            || component.InteriorWallCount != 1
            || component.ExteriorWallCount > 0
            || component.DiagonalWallCount > 0
            || component.ShortWallCount > 0
            || component.DrawingLength <= RecoverableInteriorFragmentLength(options))
        {
            return false;
        }

        var structuralNeighborhood = mainComponent.Bounds.Inflate(Math.Max(
            UnresolvedEndpointGapReviewTolerance(options) * 1.5,
            options.DefaultWallThickness * 8.0));
        return structuralNeighborhood.Intersects(component.Bounds);
    }

    private static double RecoverableInteriorFragmentLength(ScannerOptions options) =>
        Math.Max(ShortDetailComponentWallLength(options) * 1.05, options.DefaultWallThickness * 18.0);

    private static Confidence ComponentConfidence(WallGraphComponentKind kind) =>
        kind switch
        {
            WallGraphComponentKind.MainStructural => Confidence.High,
            WallGraphComponentKind.SecondaryStructural => Confidence.Medium,
            WallGraphComponentKind.ObjectLikeIsland => Confidence.Medium,
            WallGraphComponentKind.IsolatedFragment => Confidence.Low,
            _ => Confidence.Low
        };

    private static IReadOnlyList<string> ComponentEvidence(
        RawWallGraphComponent component,
        WallGraphComponentKind kind,
        RawWallGraphComponent? mainComponent,
        string? structuralTopologyExclusionReason)
    {
        var evidence = new List<string>
        {
            $"connected component with {component.WallIds.Count} wall(s), {component.NodeIds.Count} node(s), {component.EdgeIds.Count} edge(s)",
            $"drawing length {component.DrawingLength.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}",
            $"component shape: {component.HorizontalWallCount} horizontal, {component.VerticalWallCount} vertical, {component.DiagonalWallCount} diagonal, {component.ShortWallCount} short-detail wall(s)"
        };

        if (kind == WallGraphComponentKind.MainStructural)
        {
            evidence.Add("largest structural wall component on page");
        }
        else if (kind == WallGraphComponentKind.ObjectLikeIsland)
        {
            evidence.Add("compact disconnected component; review as possible object, stair/detail, or symbol linework");
        }
        else if (kind == WallGraphComponentKind.IsolatedFragment)
        {
            evidence.Add("isolated wall graph fragment with weak topology");
        }
        else if (component.PairedWallCount >= 2
            && component.DiagonalWallCount == 0
            && component.WallIds.Count <= 6)
        {
            evidence.Add("compact paired-wall component retained as secondary structural wall context");
        }
        else if (component.WallIds.Count == 1 && component.PairedWallCount == 1)
        {
            evidence.Add("anchored single paired-wall body retained as secondary structural wall context");
        }
        else if (component.WallIds.Count == 1 && component.FragmentMergedWallCount == 1 && component.InteriorWallCount == 1)
        {
            evidence.Add("long interior fragment recovered as secondary structural wall context");
        }
        else if (mainComponent is not null)
        {
            evidence.Add("connected structural component separate from the largest page component");
        }

        if (!string.IsNullOrWhiteSpace(structuralTopologyExclusionReason))
        {
            evidence.Add("excluded from structural room/opening topology solving");
            evidence.Add($"structural topology exclusion reason: {structuralTopologyExclusionReason}");
        }

        return evidence;
    }

    private void AddComponentDiagnostics(
        ScanContext context,
        IReadOnlyList<WallGraphComponent> components)
    {
        if (components.Count == 0)
        {
            return;
        }

        var mainCount = components.Count(component => component.Kind == WallGraphComponentKind.MainStructural);
        var secondaryCount = components.Count(component => component.Kind == WallGraphComponentKind.SecondaryStructural);
        var objectLikeCount = components.Count(component => component.Kind == WallGraphComponentKind.ObjectLikeIsland);
        var isolatedCount = components.Count(component => component.Kind == WallGraphComponentKind.IsolatedFragment);
        var excludedCount = components.Count(component => component.ExcludedFromStructuralTopology);
        var excludedObjectLikeCount = components.Count(component =>
            component.ExcludedFromStructuralTopology
            && component.Kind == WallGraphComponentKind.ObjectLikeIsland);
        var excludedIsolatedCount = components.Count(component =>
            component.ExcludedFromStructuralTopology
            && component.Kind == WallGraphComponentKind.IsolatedFragment);
        var largest = components
            .OrderByDescending(component => component.DrawingLength)
            .ThenByDescending(component => component.WallCount)
            .First();

        context.AddDiagnostic(
            "wall_graph.components.detected",
            DiagnosticSeverity.Info,
            Name,
            "Wall graph connected components were summarized for topology review.",
            confidence: Confidence.Medium,
            properties: new Dictionary<string, string>
            {
                ["componentCount"] = components.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["mainStructuralComponentCount"] = mainCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["secondaryStructuralComponentCount"] = secondaryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["objectLikeIslandCount"] = objectLikeCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["isolatedFragmentCount"] = isolatedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["excludedStructuralTopologyComponentCount"] = excludedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["excludedObjectLikeIslandCount"] = excludedObjectLikeCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["excludedIsolatedFragmentCount"] = excludedIsolatedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["largestComponentId"] = largest.Id,
                ["largestComponentWallCount"] = largest.WallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["largestComponentDrawingLength"] = largest.DrawingLength.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            });

        if (objectLikeCount > 0)
        {
            context.AddDiagnostic(
                "wall_graph.object_like_components.review",
                DiagnosticSeverity.Info,
                Name,
                "Compact disconnected wall graph components may be object or symbol linework rather than walls.",
                confidence: Confidence.Medium,
                properties: new Dictionary<string, string>
                {
                    ["objectLikeIslandCount"] = objectLikeCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["componentIds"] = string.Join(",", components
                        .Where(component => component.Kind == WallGraphComponentKind.ObjectLikeIsland)
                        .Select(component => component.Id)
                        .Take(20))
                });
        }

        if (excludedIsolatedCount > 0)
        {
            context.AddDiagnostic(
                "wall_graph.weak_fragments.excluded",
                DiagnosticSeverity.Info,
                Name,
                "Weak isolated wall fragments were kept in wall exports but excluded from structural topology solving.",
                confidence: Confidence.Medium,
                properties: new Dictionary<string, string>
                {
                    ["excludedIsolatedFragmentCount"] = excludedIsolatedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["componentIds"] = string.Join(",", components
                        .Where(component =>
                            component.ExcludedFromStructuralTopology
                            && component.Kind == WallGraphComponentKind.IsolatedFragment)
                        .Select(component => component.Id)
                        .Take(20))
                });
        }

    }

    private void AddSurfacePatternWallOverlapDiagnostics(
        ScanContext context,
        IReadOnlyList<WallGraphComponent> components)
    {
        if (context.SurfacePatterns.Count == 0 || context.Walls.Count == 0)
        {
            return;
        }

        var componentsByWallId = new Dictionary<string, WallGraphComponent>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            foreach (var wallId in component.WallIds)
            {
                componentsByWallId.TryAdd(wallId, component);
            }
        }

        var candidates = new List<SurfacePatternWallOverlapCandidate>();
        foreach (var pattern in context.SurfacePatterns.Where(pattern => pattern.ExcludedFromStructuralTopology))
        {
            var patternSourceIds = pattern.SourcePrimitiveIds.ToHashSet(StringComparer.Ordinal);
            foreach (var wall in context.Walls.Where(wall => wall.PageNumber == pattern.PageNumber))
            {
                componentsByWallId.TryGetValue(wall.Id, out var component);
                if (component?.ExcludedFromStructuralTopology == true)
                {
                    continue;
                }

                var wallBounds = wall.Bounds;
                if (!pattern.Bounds.Intersects(wallBounds))
                {
                    continue;
                }

                var intersection = pattern.Bounds.Intersection(wallBounds);
                if (intersection.IsEmpty || intersection.Area <= 0)
                {
                    continue;
                }

                var wallOverlapRatio = intersection.Area / Math.Max(1, wallBounds.Area);
                var patternOverlapRatio = intersection.Area / Math.Max(1, pattern.Bounds.Area);
                var sharedSourceIds = wall.SourcePrimitiveIds
                    .Where(patternSourceIds.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                if (!ShouldReviewSurfacePatternWallOverlap(
                    wall,
                    pattern,
                    wallOverlapRatio,
                    patternOverlapRatio,
                    sharedSourceIds.Length))
                {
                    continue;
                }

                candidates.Add(new SurfacePatternWallOverlapCandidate(
                    pattern,
                    wall,
                    component,
                    intersection,
                    wallOverlapRatio,
                    patternOverlapRatio,
                    sharedSourceIds));
            }
        }

        foreach (var candidate in candidates
                     .OrderBy(candidate => candidate.PageNumber)
                     .ThenByDescending(candidate => candidate.PriorityScore)
                     .ThenBy(candidate => candidate.Pattern.Id, StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.Wall.Id, StringComparer.Ordinal)
                     .Take(ScanReviewQueueSummary.SurfacePatternWallOverlapReviewQueueLimit))
        {
            var sourcePrimitiveIds = candidate.SharedSourcePrimitiveIds.Count > 0
                ? candidate.SharedSourcePrimitiveIds
                : candidate.Wall.SourcePrimitiveIds;

            context.AddDiagnostic(
                "wall_graph.surface_pattern_wall_overlap.review",
                DiagnosticSeverity.Warning,
                Name,
                "A non-excluded wall overlaps or shares source primitives with dense non-structural surface/detail linework.",
                candidate.PageNumber,
                candidate.IntersectionBounds,
                Confidence.Medium,
                DiagnosticScope.Detection,
                sourcePrimitiveIds,
                properties: new Dictionary<string, string>
                {
                    ["surfacePatternId"] = candidate.Pattern.Id,
                    ["surfacePatternKind"] = candidate.Pattern.Kind.ToString(),
                    ["surfacePatternOrientation"] = candidate.Pattern.Orientation.ToString(),
                    ["wallId"] = candidate.Wall.Id,
                    ["wallComponentId"] = candidate.Component?.Id ?? string.Empty,
                    ["wallComponentKind"] = candidate.Component?.Kind.ToString() ?? string.Empty,
                    ["excludedFromStructuralTopology"] = (candidate.Component?.ExcludedFromStructuralTopology ?? false).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["wallOverlapRatio"] = candidate.WallOverlapRatio.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["patternOverlapRatio"] = candidate.PatternOverlapRatio.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["sharedSourcePrimitiveCount"] = candidate.SharedSourcePrimitiveIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["wallSourcePrimitiveCount"] = candidate.Wall.SourcePrimitiveIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["surfacePatternSourcePrimitiveCount"] = candidate.Pattern.SourcePrimitiveIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["wallLength"] = candidate.Wall.DrawingLength.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["surfacePatternBounds"] = FormatRect(candidate.Pattern.Bounds),
                    ["wallBounds"] = FormatRect(candidate.Wall.Bounds)
                });
        }
    }

    private static bool ShouldReviewSurfacePatternWallOverlap(
        WallSegment wall,
        SurfacePatternCandidate pattern,
        double wallOverlapRatio,
        double patternOverlapRatio,
        int sharedSourcePrimitiveCount)
    {
        if (sharedSourcePrimitiveCount >= 2)
        {
            return true;
        }

        if (sharedSourcePrimitiveCount >= 1 && wallOverlapRatio >= 0.10)
        {
            return true;
        }

        var patternLongSide = Math.Max(pattern.Bounds.Width, pattern.Bounds.Height);
        var maximumLocalWallLength = Math.Max(64, patternLongSide * 0.75);
        if (wallOverlapRatio >= 0.85 && wall.DrawingLength <= maximumLocalWallLength)
        {
            return true;
        }

        return wallOverlapRatio >= 0.50
            && patternOverlapRatio >= 0.015
            && wall.DrawingLength <= maximumLocalWallLength;
    }

    private static string FormatRect(PlanRect rect) =>
        string.Join(
            ",",
            rect.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            rect.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            rect.Width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            rect.Height.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

    private sealed record SurfacePatternWallOverlapCandidate(
        SurfacePatternCandidate Pattern,
        WallSegment Wall,
        WallGraphComponent? Component,
        PlanRect IntersectionBounds,
        double WallOverlapRatio,
        double PatternOverlapRatio,
        IReadOnlyList<string> SharedSourcePrimitiveIds)
    {
        public int PageNumber => Pattern.PageNumber;

        public double PriorityScore
        {
            get
            {
                var score = WallOverlapRatio * 100;
                score += Math.Min(SharedSourcePrimitiveIds.Count, 10) * 30;
                if (Component?.Kind == WallGraphComponentKind.MainStructural)
                {
                    score += 20;
                }

                return score;
            }
        }
    }

    private static IReadOnlyList<WallGraphRepairCandidate> DetectUnresolvedEndpointGaps(
        IReadOnlyList<WallNode> nodes,
        IReadOnlyList<WallEdge> edges,
        IReadOnlyList<WallGraphComponent> components,
        IReadOnlyList<WallSegment> walls,
        IReadOnlySet<string> repairCandidateWallIds,
        ScannerOptions options,
        out int suppressedReviewEndpointGapCount)
    {
        suppressedReviewEndpointGapCount = 0;
        if (options.MaxWallGraphEndpointGapReviewItems <= 0)
        {
            return Array.Empty<WallGraphRepairCandidate>();
        }

        var autoConnectTolerance = InferredNearTouchJunctionTolerance(options);
        var minimumReviewDistance = autoConnectTolerance;
        var reviewTolerance = UnresolvedEndpointGapReviewTolerance(options);
        if (reviewTolerance <= autoConnectTolerance)
        {
            return Array.Empty<WallGraphRepairCandidate>();
        }

        var wallsById = walls.ToDictionary(wall => wall.Id, StringComparer.Ordinal);
        var componentByWallId = BuildComponentByWallId(components);
        var incidentEdgesByNode = nodes.ToDictionary(
            node => node.Id,
            _ => new List<WallEdge>(),
            StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (incidentEdgesByNode.TryGetValue(edge.FromNodeId, out var fromEdges))
            {
                fromEdges.Add(edge);
            }

            if (incidentEdgesByNode.TryGetValue(edge.ToNodeId, out var toEdges))
            {
                toEdges.Add(edge);
            }
        }

        var endpointNodes = nodes
            .Where(node => node.Degree <= 1 || node.Kind == WallNodeKind.Endpoint)
            .OrderBy(node => node.PageNumber)
            .ThenBy(node => node.Id, StringComparer.Ordinal)
            .ToArray();
        var candidates = new List<WallGraphRepairCandidate>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in endpointNodes)
        {
            var nodeWallIds = WallIdsForNode(node.Id, incidentEdgesByNode)
                .ToHashSet(StringComparer.Ordinal);
            if (nodeWallIds.Count == 0)
            {
                continue;
            }

            if (ContainsObjectLikeWall(nodeWallIds, componentByWallId))
            {
                continue;
            }

            WallGraphRepairCandidate? best = null;

            foreach (var wall in walls
                         .Where(wall => wall.PageNumber == node.PageNumber)
                         .Where(wall => !nodeWallIds.Contains(wall.Id)))
            {
                if (!IsEndpointDirectionPerpendicularToLine(node, wall.CenterLine))
                {
                    continue;
                }

                var hostLength = Math.Max(wall.CenterLine.Length, 1);
                var parameterTolerance = reviewTolerance / hostLength;
                var parameter = wall.CenterLine.ProjectParameter(node.Position);
                if (parameter < -parameterTolerance || parameter > 1 + parameterTolerance)
                {
                    continue;
                }

                var projected = wall.CenterLine.PointAt(Math.Clamp(parameter, 0, 1));
                var distance = node.Position.DistanceTo(projected);
                if (distance <= minimumReviewDistance || distance > reviewTolerance)
                {
                    continue;
                }

                var involvedWallIds = nodeWallIds.Concat(new[] { wall.Id }).ToArray();
                if (!CanCreateEndpointGapRepairCandidate(involvedWallIds, repairCandidateWallIds))
                {
                    suppressedReviewEndpointGapCount++;
                    continue;
                }

                if (!ShouldReviewEndpointGap(involvedWallIds, componentByWallId))
                {
                    continue;
                }

                var key = $"wall:{node.Id}:{wall.Id}";
                if (!keys.Contains(key))
                {
                    best = ChooseNearest(
                        best,
                        CreateEndpointGapReview(
                            node,
                            projected,
                            targetNodeId: null,
                            hostWallId: wall.Id,
                            WallGraphRepairCandidateKind.EndpointToWall,
                            distance,
                            involvedWallIds,
                            wallsById,
                            options));
                }
            }

            foreach (var other in endpointNodes
                         .Where(other => other.PageNumber == node.PageNumber)
                         .Where(other => string.CompareOrdinal(other.Id, node.Id) > 0))
            {
                var otherWallIds = WallIdsForNode(other.Id, incidentEdgesByNode)
                    .ToHashSet(StringComparer.Ordinal);
                if (otherWallIds.Count == 0 || nodeWallIds.Overlaps(otherWallIds))
                {
                    continue;
                }

                var involvedWallIds = nodeWallIds.Concat(otherWallIds).ToArray();
                if (!CanCreateEndpointGapRepairCandidate(involvedWallIds, repairCandidateWallIds))
                {
                    suppressedReviewEndpointGapCount++;
                    continue;
                }

                if (!ShouldReviewEndpointGap(involvedWallIds, componentByWallId))
                {
                    continue;
                }

                if (TryGetCollinearContinuationGap(
                        node,
                        other,
                        minimumReviewDistance,
                        reviewTolerance,
                        options,
                        out var collinearDistance,
                        out var axisOffset,
                        out var axisName))
                {
                    var collinearPairKey = string.CompareOrdinal(node.Id, other.Id) < 0
                        ? $"node:{node.Id}:{other.Id}"
                        : $"node:{other.Id}:{node.Id}";
                    if (!keys.Contains(collinearPairKey))
                    {
                        best = ChooseNearest(
                            best,
                            CreateEndpointGapReview(
                                node,
                                other.Position,
                                other.Id,
                                hostWallId: null,
                                WallGraphRepairCandidateKind.EndpointToEndpoint,
                                collinearDistance,
                                involvedWallIds,
                                wallsById,
                                options,
                                new[]
                                {
                                    $"{axisName} collinear continuation gap",
                                    $"endpoint axis offset {axisOffset.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} drawing units",
                                    "endpoints face away from the gap; review before bridging possible missing wall continuity"
                                }));
                    }

                    continue;
                }

                if (!HasPerpendicularDirections(node, other))
                {
                    continue;
                }

                var distance = node.Position.DistanceTo(other.Position);
                if (distance <= minimumReviewDistance || distance > reviewTolerance)
                {
                    continue;
                }

                var pairKey = string.CompareOrdinal(node.Id, other.Id) < 0
                    ? $"node:{node.Id}:{other.Id}"
                    : $"node:{other.Id}:{node.Id}";
                if (!keys.Contains(pairKey))
                {
                    best = ChooseNearest(
                        best,
                        CreateEndpointGapReview(
                            node,
                            other.Position,
                            other.Id,
                            hostWallId: null,
                            WallGraphRepairCandidateKind.EndpointToEndpoint,
                            distance,
                            involvedWallIds,
                            wallsById,
                            options));
                }
            }

            if (best is null)
            {
                continue;
            }

            var bestKey = best.Kind == WallGraphRepairCandidateKind.EndpointToWall
                ? $"wall:{best.SourceNodeId}:{best.HostWallId}"
                : string.CompareOrdinal(best.SourceNodeId, best.TargetNodeId) < 0
                    ? $"node:{best.SourceNodeId}:{best.TargetNodeId}"
                    : $"node:{best.TargetNodeId}:{best.SourceNodeId}";
            if (keys.Add(bestKey))
            {
                candidates.Add(best);
            }
        }

        return candidates
            .OrderBy(candidate => candidate.PageNumber)
            .ThenBy(candidate => candidate.GapDistance)
            .ThenBy(candidate => candidate.SourceNodeId, StringComparer.Ordinal)
            .GroupBy(candidate => EndpointGapDedupeKey(candidate, Math.Max(2, options.WallSnapTolerance * 2.0)), StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(options.MaxWallGraphEndpointGapReviewItems)
            .ToArray();
    }

    private static bool CanCreateEndpointGapRepairCandidate(
        IEnumerable<string> wallIds,
        IReadOnlySet<string> repairCandidateWallIds) =>
        wallIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .All(repairCandidateWallIds.Contains);

    private static string EndpointGapDedupeKey(WallGraphRepairCandidate gap, double bucketSize)
    {
        var center = gap.Bounds.Center;
        var xBucket = Math.Round(center.X / bucketSize);
        var yBucket = Math.Round(center.Y / bucketSize);
        return string.Join(
            ":",
            gap.PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(",", gap.WallIds),
            xBucket.ToString(System.Globalization.CultureInfo.InvariantCulture),
            yBucket.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static IReadOnlyDictionary<string, WallGraphComponent> BuildComponentByWallId(
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

    private static bool ShouldReviewEndpointGap(
        IEnumerable<string> wallIds,
        IReadOnlyDictionary<string, WallGraphComponent> componentByWallId)
    {
        var components = wallIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => componentByWallId.TryGetValue(id, out var component) ? component : null)
            .Where(component => component is not null)
            .Cast<WallGraphComponent>()
            .DistinctBy(component => component.Id)
            .ToArray();

        if (components.Any(component =>
                component.ExcludedFromStructuralTopology
                || component.Kind == WallGraphComponentKind.ObjectLikeIsland))
        {
            return false;
        }

        return components.Any(component =>
            component.Kind is WallGraphComponentKind.MainStructural or WallGraphComponentKind.SecondaryStructural);
    }

    private static bool ContainsObjectLikeWall(
        IEnumerable<string> wallIds,
        IReadOnlyDictionary<string, WallGraphComponent> componentByWallId) =>
        wallIds.Any(id =>
            componentByWallId.TryGetValue(id, out var component)
            && (component.ExcludedFromStructuralTopology
                || component.Kind == WallGraphComponentKind.ObjectLikeIsland));

    private static IReadOnlyList<WallGraphRepairCandidate> DetectEndpointOverrunRepairCandidates(
        IReadOnlyList<EndpointOverrunReview> reviews,
        IReadOnlyList<WallNode> nodes,
        IReadOnlyList<WallGraphComponent> components,
        IReadOnlyList<WallSegment> walls,
        ScannerOptions options)
    {
        if (reviews.Count == 0 || options.MaxWallGraphEndpointGapReviewItems <= 0)
        {
            return Array.Empty<WallGraphRepairCandidate>();
        }

        var wallsById = walls.ToDictionary(wall => wall.Id, StringComparer.Ordinal);
        var componentByWallId = BuildComponentByWallId(components);
        var candidates = new List<WallGraphRepairCandidate>();
        foreach (var review in reviews)
        {
            var involvedWallIds = new[] { review.WallId }
                .Concat(review.SupportWallIds)
                .Where(id => !string.IsNullOrWhiteSpace(id) && wallsById.ContainsKey(id))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (!ShouldReviewEndpointGap(involvedWallIds, componentByWallId))
            {
                continue;
            }

            candidates.Add(CreateEndpointOverrunReview(
                review,
                FindNodeId(nodes, review.PageNumber, review.Endpoint, options.WallSnapTolerance)
                    ?? $"wall:{review.WallId}:overrun-endpoint",
                FindNodeId(nodes, review.PageNumber, review.JunctionPoint, options.WallSnapTolerance),
                involvedWallIds,
                wallsById,
                options));
        }

        return candidates
            .OrderBy(candidate => candidate.PageNumber)
            .ThenBy(candidate => candidate.GapDistance)
            .ThenBy(candidate => candidate.SourceNodeId, StringComparer.Ordinal)
            .GroupBy(candidate => EndpointGapDedupeKey(candidate, Math.Max(2, options.WallSnapTolerance * 2.0)), StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(options.MaxWallGraphEndpointGapReviewItems)
            .ToArray();
    }

    private static string? FindNodeId(
        IReadOnlyList<WallNode> nodes,
        int pageNumber,
        PlanPoint point,
        double tolerance) =>
        nodes
            .Where(node => node.PageNumber == pageNumber)
            .OrderBy(node => node.Position.DistanceTo(point))
            .FirstOrDefault(node => node.Position.DistanceTo(point) <= Math.Max(tolerance, 0.5))
            ?.Id;

    private static WallGraphRepairCandidate? ChooseNearest(WallGraphRepairCandidate? current, WallGraphRepairCandidate candidate) =>
        current is null || candidate.GapDistance < current.GapDistance
            ? candidate
            : current;

    private static WallGraphRepairCandidate CreateEndpointGapReview(
        WallNode node,
        PlanPoint targetPoint,
        string? targetNodeId,
        string? hostWallId,
        WallGraphRepairCandidateKind kind,
        double distance,
        IEnumerable<string> wallIds,
        IReadOnlyDictionary<string, WallSegment> wallsById,
        ScannerOptions options,
        IReadOnlyList<string>? extraEvidence = null)
    {
        var orderedWallIds = wallIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && wallsById.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var sourcePrimitiveIds = orderedWallIds
            .SelectMany(id => wallsById[id].SourcePrimitiveIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var inflation = Math.Max(options.DefaultWallThickness, options.WallSnapTolerance * 3.0);
        var bounds = PlanRect.FromPoints(node.Position, targetPoint).Inflate(inflation);
        var evidence = new[]
        {
            $"{kind} gap {distance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} drawing units",
            "outside safe snap tolerance; review before inferring a wall junction",
            $"candidate wall ids: {string.Join(",", orderedWallIds)}"
        };

        var action = kind switch
        {
            WallGraphRepairCandidateKind.EndpointToWall => WallGraphRepairAction.SnapEndpointToWall,
            WallGraphRepairCandidateKind.EndpointToEndpoint => WallGraphRepairAction.SnapEndpointToEndpoint,
            WallGraphRepairCandidateKind.EndpointOverrun => WallGraphRepairAction.TrimEndpointOverrun,
            _ => WallGraphRepairAction.SnapEndpointToEndpoint
        };
        var safeSnapDistance = InferredNearTouchJunctionTolerance(options);
        var reviewDistanceLimit = UnresolvedEndpointGapReviewTolerance(options);
        var excessDistanceBeyondSafeSnap = Math.Max(0, distance - safeSnapDistance);
        var severity = AssessRepairSeverity(kind, distance, safeSnapDistance, reviewDistanceLimit);
        var importImpact = severity == WallGraphRepairSeverity.High
            ? WallGraphRepairImportImpact.TopologyImportBlocked
            : WallGraphRepairImportImpact.TopologyReviewRequired;
        var applicability = severity == WallGraphRepairSeverity.High
            ? WallGraphRepairApplicability.ManualCorrectionRecommended
            : WallGraphRepairApplicability.ReviewAndApplySuggestedSnap;
        evidence =
        [
            .. evidence,
            .. (extraEvidence ?? Array.Empty<string>()),
            $"safe auto-snap distance {safeSnapDistance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} drawing units",
            $"review distance limit {reviewDistanceLimit.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} drawing units",
            $"excess beyond safe snap {excessDistanceBeyondSafeSnap.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} drawing units",
            $"repair assessment {severity} / {importImpact} / {applicability}"
        ];

        return new WallGraphRepairCandidate(
            RepairCandidateId(node.PageNumber, node.Id, targetNodeId, hostWallId, kind),
            node.PageNumber,
            kind,
            action,
            severity,
            importImpact,
            applicability,
            node.Id,
            node.Position,
            targetPoint,
            targetNodeId,
            hostWallId,
            distance,
            safeSnapDistance,
            reviewDistanceLimit,
            excessDistanceBeyondSafeSnap,
            new PlanLineSegment(node.Position, targetPoint),
            bounds,
            orderedWallIds,
            sourcePrimitiveIds,
            Confidence.Medium,
            true,
            evidence);
    }

    private static WallGraphRepairCandidate CreateEndpointOverrunReview(
        EndpointOverrunReview review,
        string sourceNodeId,
        string? targetNodeId,
        IReadOnlyList<string> wallIds,
        IReadOnlyDictionary<string, WallSegment> wallsById,
        ScannerOptions options)
    {
        var sourcePrimitiveIds = wallIds
            .SelectMany(id => wallsById[id].SourcePrimitiveIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var excessDistanceBeyondSafeTrim = Math.Max(0, review.TailLength - review.AutoTrimLimit);
        var severity = AssessRepairSeverity(
            WallGraphRepairCandidateKind.EndpointOverrun,
            review.TailLength,
            review.AutoTrimLimit,
            review.ReviewLimit);
        var importImpact = severity == WallGraphRepairSeverity.High
            ? WallGraphRepairImportImpact.TopologyImportBlocked
            : WallGraphRepairImportImpact.TopologyReviewRequired;
        var applicability = severity == WallGraphRepairSeverity.High
            ? WallGraphRepairApplicability.ManualCorrectionRecommended
            : WallGraphRepairApplicability.ReviewAndApplySuggestedTrim;
        var inflation = Math.Max(options.DefaultWallThickness, options.WallSnapTolerance * 3.0);
        var evidence = new[]
        {
            $"EndpointOverrun tail {review.TailLength.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} drawing units",
            "supported junction suggests a possible overextended wall endpoint",
            "tail is outside safe auto-trim tolerance; review before trimming placement geometry",
            $"safe auto-trim distance {review.AutoTrimLimit.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} drawing units",
            $"review distance limit {review.ReviewLimit.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} drawing units",
            $"excess beyond safe trim {excessDistanceBeyondSafeTrim.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} drawing units",
            $"support wall ids: {string.Join(",", review.SupportWallIds)}",
            $"repair assessment {severity} / {importImpact} / {applicability}"
        };

        return new WallGraphRepairCandidate(
            RepairCandidateId(review.PageNumber, sourceNodeId, targetNodeId, review.WallId, WallGraphRepairCandidateKind.EndpointOverrun),
            review.PageNumber,
            WallGraphRepairCandidateKind.EndpointOverrun,
            WallGraphRepairAction.TrimEndpointOverrun,
            severity,
            importImpact,
            applicability,
            sourceNodeId,
            review.Endpoint,
            review.JunctionPoint,
            targetNodeId,
            review.WallId,
            review.TailLength,
            review.AutoTrimLimit,
            review.ReviewLimit,
            excessDistanceBeyondSafeTrim,
            new PlanLineSegment(review.Endpoint, review.JunctionPoint),
            PlanRect.FromPoints(review.Endpoint, review.JunctionPoint).Inflate(inflation),
            wallIds,
            sourcePrimitiveIds,
            Confidence.Medium,
            true,
            evidence);
    }

    private static WallGraphRepairSeverity AssessRepairSeverity(
        WallGraphRepairCandidateKind kind,
        double distance,
        double safeSnapDistance,
        double reviewDistanceLimit)
    {
        var span = Math.Max(0.001, reviewDistanceLimit - safeSnapDistance);
        var normalizedExcess = Math.Clamp((distance - safeSnapDistance) / span, 0, 1);
        if (normalizedExcess >= 0.75)
        {
            return WallGraphRepairSeverity.High;
        }

        if (normalizedExcess >= 0.35 || kind == WallGraphRepairCandidateKind.EndpointToEndpoint)
        {
            return WallGraphRepairSeverity.Medium;
        }

        return WallGraphRepairSeverity.Low;
    }

    private static string RepairCandidateId(
        int pageNumber,
        string sourceNodeId,
        string? targetNodeId,
        string? hostWallId,
        WallGraphRepairCandidateKind kind)
    {
        var targetId = targetNodeId ?? hostWallId ?? "unknown-target";
        return string.Join(
            ":",
            "page",
            pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "wall-graph-repair",
            kind.ToString(),
            sourceNodeId,
            targetId);
    }

    private static void AddEndpointGapDiagnostics(
        ScanContext context,
        IReadOnlyList<WallGraphRepairCandidate> endpointGaps)
    {
        if (endpointGaps.Count == 0)
        {
            return;
        }

        context.AddDiagnostic(
            "wall_graph.endpoint_gaps.detected",
            DiagnosticSeverity.Warning,
            "wall-graph",
            "Possible unsnapped wall graph endpoint gaps were found and queued for review.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: endpointGaps.SelectMany(gap => gap.SourcePrimitiveIds),
            properties: new Dictionary<string, string>
            {
                ["gapCount"] = endpointGaps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["minGapDistance"] = endpointGaps.Min(gap => gap.GapDistance).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                ["maxGapDistance"] = endpointGaps.Max(gap => gap.GapDistance).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                ["gapKinds"] = string.Join(",", endpointGaps.Select(gap => gap.Kind.ToString()).Distinct(StringComparer.Ordinal))
            });

        foreach (var gap in endpointGaps)
        {
            context.AddDiagnostic(
                "wall_graph.endpoint_gap.review",
                DiagnosticSeverity.Warning,
                "wall-graph",
                "A wall graph endpoint nearly touches another wall endpoint or host wall but was not safely snapped.",
                pageNumber: gap.PageNumber,
                region: gap.Bounds,
                confidence: Confidence.Medium,
                scope: DiagnosticScope.Detection,
                sourcePrimitiveIds: gap.SourcePrimitiveIds,
                properties: new Dictionary<string, string>
                {
                    ["gapKind"] = gap.Kind.ToString(),
                    ["gapDistance"] = gap.GapDistance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["safeSnapDistance"] = gap.SafeSnapDistance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["reviewDistanceLimit"] = gap.ReviewDistanceLimit.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["excessDistanceBeyondSafeSnap"] = gap.ExcessDistanceBeyondSafeSnap.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["severity"] = gap.Severity.ToString(),
                    ["importImpact"] = gap.ImportImpact.ToString(),
                    ["applicability"] = gap.Applicability.ToString(),
                    ["repairCandidateId"] = gap.Id,
                    ["suggestedAction"] = gap.SuggestedAction.ToString(),
                    ["nodeId"] = gap.SourceNodeId,
                    ["targetNodeId"] = gap.TargetNodeId ?? string.Empty,
                    ["hostWallId"] = gap.HostWallId ?? string.Empty,
                    ["nodeX"] = gap.SourcePoint.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["nodeY"] = gap.SourcePoint.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["targetX"] = gap.TargetPoint.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["targetY"] = gap.TargetPoint.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["wallIds"] = string.Join(",", gap.WallIds)
                });
        }
    }

    private static void AddEndpointOverrunDiagnostics(
        ScanContext context,
        IReadOnlyList<WallGraphRepairCandidate> endpointOverruns)
    {
        if (endpointOverruns.Count == 0)
        {
            return;
        }

        context.AddDiagnostic(
            "wall_graph.endpoint_overruns.detected",
            DiagnosticSeverity.Warning,
            "wall-graph",
            "Possible overextended wall endpoints were found and queued for trim review.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: endpointOverruns.SelectMany(candidate => candidate.SourcePrimitiveIds),
            properties: new Dictionary<string, string>
            {
                ["overrunCount"] = endpointOverruns.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["minOverrunDistance"] = endpointOverruns.Min(candidate => candidate.GapDistance).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                ["maxOverrunDistance"] = endpointOverruns.Max(candidate => candidate.GapDistance).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                ["candidateKinds"] = string.Join(",", endpointOverruns.Select(candidate => candidate.Kind.ToString()).Distinct(StringComparer.Ordinal))
            });

        foreach (var overrun in endpointOverruns)
        {
            context.AddDiagnostic(
                "wall_graph.endpoint_overrun.review",
                DiagnosticSeverity.Warning,
                "wall-graph",
                "A wall endpoint extends beyond a supported junction but was too long to trim automatically.",
                pageNumber: overrun.PageNumber,
                region: overrun.Bounds,
                confidence: Confidence.Medium,
                scope: DiagnosticScope.Detection,
                sourcePrimitiveIds: overrun.SourcePrimitiveIds,
                properties: new Dictionary<string, string>
                {
                    ["overrunKind"] = overrun.Kind.ToString(),
                    ["overrunDistance"] = overrun.GapDistance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["safeAutoTrimDistance"] = overrun.SafeSnapDistance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["reviewDistanceLimit"] = overrun.ReviewDistanceLimit.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["excessDistanceBeyondSafeTrim"] = overrun.ExcessDistanceBeyondSafeSnap.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["severity"] = overrun.Severity.ToString(),
                    ["importImpact"] = overrun.ImportImpact.ToString(),
                    ["applicability"] = overrun.Applicability.ToString(),
                    ["repairCandidateId"] = overrun.Id,
                    ["suggestedAction"] = overrun.SuggestedAction.ToString(),
                    ["nodeId"] = overrun.SourceNodeId,
                    ["targetNodeId"] = overrun.TargetNodeId ?? string.Empty,
                    ["wallId"] = overrun.HostWallId ?? string.Empty,
                    ["endpointX"] = overrun.SourcePoint.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["endpointY"] = overrun.SourcePoint.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["junctionX"] = overrun.TargetPoint.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["junctionY"] = overrun.TargetPoint.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["wallIds"] = string.Join(",", overrun.WallIds)
                });
        }
    }

    private void AddTopologyNormalizationDiagnostics(
        ScanContext context,
        int normalizedCollinearJunctionCount,
        int snappedEndpointGapCount,
        int trimmedEndpointOverrunCount,
        int suppressedEndpointOverrunTailEdgeCount,
        int normalizedWallSegmentCount,
        int orthogonalizedWallCenterLineCount)
    {
        if (normalizedCollinearJunctionCount == 0
            && snappedEndpointGapCount == 0
            && trimmedEndpointOverrunCount == 0
            && suppressedEndpointOverrunTailEdgeCount == 0
            && orthogonalizedWallCenterLineCount == 0)
        {
            return;
        }

        context.AddDiagnostic(
            "wall_graph.topology.normalized",
            DiagnosticSeverity.Info,
            Name,
            "Wall graph topology was normalized by straightening near-axis wall centerlines, connecting supported collinear fragments, snapping safe near-touch endpoint gaps, trimming trusted endpoint overruns, and suppressing reviewed overrun tails from clean graph edges.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            properties: new Dictionary<string, string>
            {
                ["collinearJunctionCount"] = normalizedCollinearJunctionCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["snappedEndpointGapCount"] = snappedEndpointGapCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["trimmedEndpointOverrunCount"] = trimmedEndpointOverrunCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["suppressedEndpointOverrunTailEdgeCount"] = suppressedEndpointOverrunTailEdgeCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["normalizedWallSegmentCount"] = normalizedWallSegmentCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["orthogonalizedWallCenterLineCount"] = orthogonalizedWallCenterLineCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["endpointOverrunTrimTolerance"] = EndpointOverrunTrimTolerance(context.Options).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            });
    }

    private static IReadOnlyList<string> WallIdsForNode(
        string nodeId,
        IReadOnlyDictionary<string, List<WallEdge>> incidentEdgesByNode) =>
        incidentEdgesByNode.TryGetValue(nodeId, out var incidentEdges)
            ? incidentEdges
                .Select(edge => edge.WallId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

    private static bool IsEndpointDirectionPerpendicularToLine(WallNode node, PlanLineSegment line)
    {
        var lineIsHorizontal = Math.Abs(line.End.X - line.Start.X) >= Math.Abs(line.End.Y - line.Start.Y);
        return lineIsHorizontal
            ? HasVerticalDirection(node)
            : HasHorizontalDirection(node);
    }

    private static bool HasPerpendicularDirections(WallNode first, WallNode second) =>
        HasHorizontalDirection(first) && HasVerticalDirection(second)
        || HasVerticalDirection(first) && HasHorizontalDirection(second);

    private static bool HasHorizontalDirection(WallNode node) =>
        node.Directions.Contains(nameof(DirectionBucket.East), StringComparer.Ordinal)
        || node.Directions.Contains(nameof(DirectionBucket.West), StringComparer.Ordinal);

    private static bool HasVerticalDirection(WallNode node) =>
        node.Directions.Contains(nameof(DirectionBucket.North), StringComparer.Ordinal)
        || node.Directions.Contains(nameof(DirectionBucket.South), StringComparer.Ordinal);

    private static bool TryGetCollinearContinuationGap(
        WallNode first,
        WallNode second,
        double minimumReviewDistance,
        double reviewTolerance,
        ScannerOptions options,
        out double distance,
        out double axisOffset,
        out string axisName)
    {
        distance = 0;
        axisOffset = 0;
        axisName = string.Empty;

        var dx = second.Position.X - first.Position.X;
        var dy = second.Position.Y - first.Position.Y;
        var axisTolerance = CollinearContinuationAxisTolerance(options);
        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            axisName = "Horizontal";
            distance = Math.Abs(dx);
            axisOffset = Math.Abs(dy);
            if (axisOffset > axisTolerance
                || distance <= minimumReviewDistance
                || distance > reviewTolerance)
            {
                return false;
            }

            return dx > 0
                ? HasWestDirection(first) && HasEastDirection(second)
                : HasEastDirection(first) && HasWestDirection(second);
        }

        axisName = "Vertical";
        distance = Math.Abs(dy);
        axisOffset = Math.Abs(dx);
        if (axisOffset > axisTolerance
            || distance <= minimumReviewDistance
            || distance > reviewTolerance)
        {
            return false;
        }

        return dy > 0
            ? HasNorthDirection(first) && HasSouthDirection(second)
            : HasSouthDirection(first) && HasNorthDirection(second);
    }

    private static double CollinearContinuationAxisTolerance(ScannerOptions options) =>
        Math.Max(options.WallSnapTolerance, options.DefaultWallThickness * 0.75);

    private static bool HasEastDirection(WallNode node) =>
        node.Directions.Contains(nameof(DirectionBucket.East), StringComparer.Ordinal);

    private static bool HasWestDirection(WallNode node) =>
        node.Directions.Contains(nameof(DirectionBucket.West), StringComparer.Ordinal);

    private static bool HasNorthDirection(WallNode node) =>
        node.Directions.Contains(nameof(DirectionBucket.North), StringComparer.Ordinal);

    private static bool HasSouthDirection(WallNode node) =>
        node.Directions.Contains(nameof(DirectionBucket.South), StringComparer.Ordinal);

    private static double UnresolvedEndpointGapReviewTolerance(ScannerOptions options)
    {
        var autoConnectTolerance = InferredNearTouchJunctionTolerance(options);
        var geometryLimit = Math.Max(options.MaxWallFragmentGap * 3.0, options.DefaultWallThickness * 4.0);
        var openingAwareLimit = Math.Max(autoConnectTolerance + options.WallSnapTolerance, options.MaxOpeningGap * 0.35);
        return Math.Min(Math.Max(autoConnectTolerance + options.WallSnapTolerance, geometryLimit), openingAwareLimit);
    }

    private static IReadOnlyList<PairedEndpointSnap> DetectTrustedEndpointToWallSnaps(
        IReadOnlyList<WallSegment> walls,
        IReadOnlySet<string> automaticCoordinateRepairWallIds,
        ScanContext context,
        ScannerOptions options)
    {
        var safeSnapTolerance = InferredNearTouchJunctionTolerance(options);
        var trustedSnapTolerance = TrustedEndpointSnapTolerance(options);
        if (trustedSnapTolerance <= safeSnapTolerance + 0.001)
        {
            return Array.Empty<PairedEndpointSnap>();
        }

        var candidates = new List<PairedEndpointSnapCandidate>();
        foreach (var endpointWall in walls
                     .Where(wall => automaticCoordinateRepairWallIds.Contains(wall.Id))
                     .Where(wall => IsTrustedEndpointSnapEndpointWall(wall))
                     .Where(wall => !HasOpeningKeywordEvidence(wall)))
        {
            foreach (var endpoint in new[] { endpointWall.CenterLine.Start, endpointWall.CenterLine.End })
            {
                foreach (var hostWall in walls
                             .Where(wall => wall.PageNumber == endpointWall.PageNumber)
                             .Where(wall => !string.Equals(wall.Id, endpointWall.Id, StringComparison.Ordinal))
                             .Where(wall => automaticCoordinateRepairWallIds.Contains(wall.Id))
                             .Where(IsTrustedEndpointSnapHostWall)
                             .Where(wall => !HasOpeningKeywordEvidence(wall)))
                {
                    if (!IsNearPerpendicular(endpointWall.CenterLine, hostWall.CenterLine))
                    {
                        continue;
                    }

                    var candidateSnapTolerance = TrustedEndpointSnapTolerance(endpointWall, hostWall, context, options);
                    var hostLength = Math.Max(hostWall.CenterLine.Length, 1);
                    var parameterTolerance = candidateSnapTolerance / hostLength;
                    var parameter = hostWall.CenterLine.ProjectParameter(endpoint);
                    if (parameter < -parameterTolerance || parameter > 1 + parameterTolerance)
                    {
                        continue;
                    }

                    var projected = hostWall.CenterLine.PointAt(Math.Clamp(parameter, 0, 1));
                    var distance = endpoint.DistanceTo(projected);
                    if (distance <= safeSnapTolerance || distance > candidateSnapTolerance)
                    {
                        continue;
                    }

                    var junctionPoint = PerpendicularAxisJunctionPoint(endpointWall.CenterLine, hostWall.CenterLine, projected);
                    var repairBounds = PlanRect.Union(
                            PlanRect.FromPoints(endpoint, projected),
                            PlanRect.FromPoints(endpoint, junctionPoint))
                        .Inflate(Math.Max(options.DefaultWallThickness * 2.0, options.WallSnapTolerance * 4.0));
                    var sourcePrimitiveIds = endpointWall.SourcePrimitiveIds
                        .Concat(hostWall.SourcePrimitiveIds)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    if (!IsMicroExcessEndpointSnap(distance, safeSnapTolerance, options)
                        && HasNearbyOpeningEvidence(endpointWall.PageNumber, repairBounds, sourcePrimitiveIds, context, options))
                    {
                        continue;
                    }

                    candidates.Add(new PairedEndpointSnapCandidate(
                        endpointWall,
                        hostWall,
                        endpoint,
                        junctionPoint,
                        distance));
                }
            }
        }

        return candidates
            .GroupBy(
                candidate => PointKey(candidate.EndpointWall.Id, "trusted", candidate.Endpoint, options.WallSnapTolerance),
                StringComparer.Ordinal)
            .Select(group => group.OrderBy(candidate => candidate.GapDistance).First())
            .OrderBy(candidate => candidate.EndpointWall.Id, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.HostWall.Id, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.GapDistance)
            .Select(candidate => new PairedEndpointSnap(
                candidate.EndpointWall.Id,
                candidate.HostWall.Id,
                candidate.Endpoint,
                candidate.JunctionPoint,
                candidate.GapDistance,
                new[] { candidate.HostWall.Id }))
            .ToArray();
    }

    private static bool IsTrustedEndpointSnapHostWall(WallSegment wall) =>
        wall.Confidence.Value >= 0.82
        && (wall.DetectionKind is WallDetectionKind.ParallelLinePair or WallDetectionKind.FragmentMerged
            || wall.Evidence.Any(item => item.Contains("StrongWallBody", StringComparison.OrdinalIgnoreCase)));

    private static bool IsTrustedEndpointSnapEndpointWall(WallSegment wall) =>
        wall.Confidence.Value >= 0.65
        && wall.FragmentEvidence?.RequiresGeometryReview != true
        && !wall.Evidence.Any(item =>
            item.Contains("review-only", StringComparison.OrdinalIgnoreCase)
            || item.Contains("requires review", StringComparison.OrdinalIgnoreCase)
            || item.Contains("rejected", StringComparison.OrdinalIgnoreCase));

    private static PlanPoint PerpendicularAxisJunctionPoint(
        PlanLineSegment endpointLine,
        PlanLineSegment hostLine,
        PlanPoint fallbackProjectedPoint)
    {
        var endpointHorizontal = Math.Abs(endpointLine.End.X - endpointLine.Start.X) >= Math.Abs(endpointLine.End.Y - endpointLine.Start.Y);
        var hostHorizontal = Math.Abs(hostLine.End.X - hostLine.Start.X) >= Math.Abs(hostLine.End.Y - hostLine.Start.Y);
        if (endpointHorizontal == hostHorizontal)
        {
            return fallbackProjectedPoint;
        }

        return endpointHorizontal
            ? new PlanPoint((hostLine.Start.X + hostLine.End.X) / 2.0, (endpointLine.Start.Y + endpointLine.End.Y) / 2.0)
            : new PlanPoint((endpointLine.Start.X + endpointLine.End.X) / 2.0, (hostLine.Start.Y + hostLine.End.Y) / 2.0);
    }

    private static double TrustedEndpointSnapTolerance(
        WallSegment endpointWall,
        WallSegment hostWall,
        ScanContext context,
        ScannerOptions options)
    {
        var baseTolerance = TrustedEndpointSnapTolerance(options);
        if (!IsHighTrustPairedEndpointSnapWall(endpointWall, context, options)
            || !IsTrustedEndpointSnapHostWall(hostWall))
        {
            return baseTolerance;
        }

        return Math.Max(baseTolerance, PairedEndpointSnapTolerance(options));
    }

    private static double TrustedEndpointSnapTolerance(ScannerOptions options)
    {
        var safeSnapTolerance = InferredNearTouchJunctionTolerance(options);
        var reviewTolerance = UnresolvedEndpointGapReviewTolerance(options);
        var lowRiskLimit = safeSnapTolerance + ((reviewTolerance - safeSnapTolerance) * 0.35);
        return Math.Min(
            lowRiskLimit,
            safeSnapTolerance + Math.Max(options.DefaultWallThickness, options.WallSnapTolerance * 2.0));
    }

    private static bool IsMicroExcessEndpointSnap(
        double distance,
        double safeSnapTolerance,
        ScannerOptions options) =>
        distance > safeSnapTolerance
        && distance <= safeSnapTolerance + MicroEndpointSnapExcessTolerance(options);

    private static double MicroEndpointSnapExcessTolerance(ScannerOptions options) =>
        Math.Max(0.20, options.WallSnapTolerance * 0.10);

    private static bool IsHighTrustPairedEndpointSnapWall(
        WallSegment wall,
        ScanContext context,
        ScannerOptions options)
    {
        if (wall.DrawingLength < options.MinWallLength
            || wall.Confidence.Value < 0.70
            || wall.FragmentEvidence?.RequiresGeometryReview == true
            || HasOpeningKeywordEvidence(wall))
        {
            return false;
        }

        var assessment = context.WallEvidenceMap.WallAssessments
            .FirstOrDefault(item => string.Equals(item.WallId, wall.Id, StringComparison.Ordinal));
        var evidence = (assessment?.Evidence ?? Array.Empty<string>())
            .Concat(wall.Evidence)
            .ToArray();

        if (evidence.Any(IsHardRiskReviewWallEvidence)
            || evidence.Any(IsMainStructuralPromotionBlockedEvidence)
            || evidence.Any(item => item.Contains("weak/fragmented pair evidence", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (wall.DetectionKind != WallDetectionKind.ParallelLinePair
            && !evidence.Any(item => item.Contains("parallel wall-face pair", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (assessment is null || assessment.RejectedAsNoise)
        {
            return false;
        }

        if (assessment.Category == WallEvidenceCategory.StrongWallBody
            && assessment.Confidence.Value >= 0.82
            && (assessment.Decision == WallEvidenceDecision.Accept || assessment.PlacementReady))
        {
            return true;
        }

        return assessment.Category == WallEvidenceCategory.MediumWallBody
            && assessment.Decision == WallEvidenceDecision.Review
            && assessment.Confidence.Value >= 0.82
            && IsTrustedOneEndpointMainStructuralMediumWallAssessment(assessment, wall, supportedEndpointCount: 1);
    }

    private static IReadOnlyList<PairedEndpointSnap> DetectPairedEndpointToWallSnaps(
        IReadOnlyList<WallSegment> walls,
        IReadOnlySet<string> automaticCoordinateRepairWallIds,
        ScanContext context,
        ScannerOptions options)
    {
        var safeSnapTolerance = InferredNearTouchJunctionTolerance(options);
        var pairedSnapTolerance = PairedEndpointSnapTolerance(options);
        if (pairedSnapTolerance <= safeSnapTolerance + 0.001)
        {
            return Array.Empty<PairedEndpointSnap>();
        }

        var rawCandidates = new List<PairedEndpointSnapCandidate>();
        foreach (var endpointWall in walls
                     .Where(wall => automaticCoordinateRepairWallIds.Contains(wall.Id))
                     .Where(wall => !HasOpeningKeywordEvidence(wall)))
        {
            foreach (var endpoint in new[] { endpointWall.CenterLine.Start, endpointWall.CenterLine.End })
            {
                foreach (var hostWall in walls
                             .Where(wall => wall.PageNumber == endpointWall.PageNumber)
                             .Where(wall => !string.Equals(wall.Id, endpointWall.Id, StringComparison.Ordinal))
                             .Where(wall => automaticCoordinateRepairWallIds.Contains(wall.Id))
                             .Where(wall => !HasOpeningKeywordEvidence(wall)))
                {
                    if (!IsNearPerpendicular(endpointWall.CenterLine, hostWall.CenterLine))
                    {
                        continue;
                    }

                    var hostLength = Math.Max(hostWall.CenterLine.Length, 1);
                    var parameterTolerance = pairedSnapTolerance / hostLength;
                    var parameter = hostWall.CenterLine.ProjectParameter(endpoint);
                    if (parameter < -parameterTolerance || parameter > 1 + parameterTolerance)
                    {
                        continue;
                    }

                    var projected = hostWall.CenterLine.PointAt(Math.Clamp(parameter, 0, 1));
                    var distance = endpoint.DistanceTo(projected);
                    if (distance <= safeSnapTolerance || distance > pairedSnapTolerance)
                    {
                        continue;
                    }

                    var junctionPoint = PerpendicularAxisJunctionPoint(endpointWall.CenterLine, hostWall.CenterLine, projected);
                    var repairBounds = PlanRect.Union(
                            PlanRect.FromPoints(endpoint, projected),
                            PlanRect.FromPoints(endpoint, junctionPoint))
                        .Inflate(Math.Max(options.DefaultWallThickness * 2.0, options.WallSnapTolerance * 4.0));
                    var sourcePrimitiveIds = endpointWall.SourcePrimitiveIds
                        .Concat(hostWall.SourcePrimitiveIds)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    if (HasNearbyOpeningEvidence(endpointWall.PageNumber, repairBounds, sourcePrimitiveIds, context, options))
                    {
                        continue;
                    }

                    rawCandidates.Add(
                        new PairedEndpointSnapCandidate(
                            endpointWall,
                            hostWall,
                            endpoint,
                            junctionPoint,
                            distance));
                }
            }
        }

        if (rawCandidates.Count < 2)
        {
            return Array.Empty<PairedEndpointSnap>();
        }

        var approved = new List<PairedEndpointSnap>();
        var approvedKeys = new HashSet<string>(StringComparer.Ordinal);
        var supportSeparation = PairedEndpointSupportSeparation(options);
        var supportGapTolerance = Math.Max(options.DefaultWallThickness, options.WallSnapTolerance * 2.0);

        foreach (var candidate in rawCandidates)
        {
            var supports = rawCandidates
                .Where(other => !ReferenceEquals(other, candidate))
                .Where(other => string.Equals(other.HostWall.Id, candidate.HostWall.Id, StringComparison.Ordinal))
                .Where(other => !string.Equals(other.EndpointWall.Id, candidate.EndpointWall.Id, StringComparison.Ordinal))
                .Where(other => IsNearParallel(candidate.EndpointWall.CenterLine, other.EndpointWall.CenterLine))
                .Where(other => candidate.JunctionPoint.DistanceTo(other.JunctionPoint) <= supportSeparation)
                .Where(other => Math.Abs(candidate.GapDistance - other.GapDistance) <= supportGapTolerance)
                .Where(other => EndpointsAreOnSameHostSide(candidate, other))
                .ToArray();
            if (supports.Length == 0)
            {
                continue;
            }

            var supportWallIds = supports
                .Select(item => item.EndpointWall.Id)
                .Append(candidate.HostWall.Id)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            AddApprovedPairedEndpointSnap(candidate, supportWallIds, approvedKeys, approved, options.WallSnapTolerance);
            foreach (var support in supports)
            {
                var reciprocalSupportWallIds = supports
                    .Where(item => !ReferenceEquals(item, support))
                    .Select(item => item.EndpointWall.Id)
                    .Append(candidate.EndpointWall.Id)
                    .Append(support.HostWall.Id)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                AddApprovedPairedEndpointSnap(support, reciprocalSupportWallIds, approvedKeys, approved, options.WallSnapTolerance);
            }
        }

        return approved
            .OrderBy(item => item.EndpointWallId, StringComparer.Ordinal)
            .ThenBy(item => item.HostWallId, StringComparer.Ordinal)
            .ThenBy(item => item.GapDistance)
            .ToArray();
    }

    private static void AddApprovedPairedEndpointSnap(
        PairedEndpointSnapCandidate candidate,
        IReadOnlyList<string> supportWallIds,
        HashSet<string> approvedKeys,
        List<PairedEndpointSnap> approved,
        double duplicateTolerance)
    {
        var key = PointKey(candidate.EndpointWall.Id, candidate.HostWall.Id, candidate.JunctionPoint, duplicateTolerance);
        if (!approvedKeys.Add(key))
        {
            return;
        }

        approved.Add(
            new PairedEndpointSnap(
                candidate.EndpointWall.Id,
                candidate.HostWall.Id,
                candidate.Endpoint,
                candidate.JunctionPoint,
                candidate.GapDistance,
                supportWallIds));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<PlanPoint>> BuildEndpointSnapPointLookup(
        IReadOnlyList<PairedEndpointSnap> snaps)
    {
        if (snaps.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<PlanPoint>>(StringComparer.Ordinal);
        }

        return snaps
            .GroupBy(snap => snap.EndpointWallId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PlanPoint>)group.Select(snap => snap.JunctionPoint).ToArray(),
                StringComparer.Ordinal);
    }

    private static bool IsApprovedPairedEndpointSnap(
        string wallId,
        PlanPoint junctionPoint,
        IReadOnlyDictionary<string, IReadOnlyList<PlanPoint>> pairedEndpointSnapPointsByWallId,
        double tolerance) =>
        pairedEndpointSnapPointsByWallId.TryGetValue(wallId, out var points)
        && points.Any(point => point.DistanceTo(junctionPoint) <= Math.Max(tolerance, 0.5));

    private static bool EndpointsAreOnSameHostSide(
        PairedEndpointSnapCandidate first,
        PairedEndpointSnapCandidate second)
    {
        var firstVector = first.Endpoint - first.JunctionPoint;
        var secondVector = second.Endpoint - second.JunctionPoint;
        if (firstVector.Length <= 0.001 || secondVector.Length <= 0.001)
        {
            return false;
        }

        return firstVector.Normalize().Dot(secondVector.Normalize()) >= 0.70;
    }

    private static bool HasNearbyOpeningEvidence(
        int pageNumber,
        PlanRect bounds,
        IReadOnlyCollection<string> excludedSourcePrimitiveIds,
        ScanContext context,
        ScannerOptions options)
    {
        var page = context.Document.Pages.FirstOrDefault(page => page.Number == pageNumber);
        if (page is null)
        {
            return false;
        }

        var searchBounds = bounds.Inflate(Math.Max(options.DefaultWallThickness * 3.0, options.WallSnapTolerance * 6.0));
        return page.Primitives
            .Where(primitive => !excludedSourcePrimitiveIds.Contains(primitive.SourceId ?? string.Empty, StringComparer.Ordinal))
            .Where(primitive => primitive.Bounds.Intersects(searchBounds))
            .Any(IsOpeningEvidencePrimitive);
    }

    private static bool HasOpeningKeywordEvidence(WallSegment wall) =>
        ContainsOpeningKeyword(wall.Id)
        || wall.SourcePrimitiveIds.Any(ContainsOpeningKeyword)
        || wall.Evidence.Any(ContainsOpeningKeyword);

    private static double PairedEndpointSnapTolerance(ScannerOptions options) =>
        Math.Min(
            UnresolvedEndpointGapReviewTolerance(options),
            InferredNearTouchJunctionTolerance(options) + Math.Max(options.DefaultWallThickness, options.WallSnapTolerance * 2.0));

    private static double PairedEndpointSupportSeparation(ScannerOptions options) =>
        Math.Max(options.DefaultWallThickness * 3.5, options.MaxWallFragmentGap * 2.0);

    private static string PointKey(
        string endpointWallId,
        string hostWallId,
        PlanPoint point,
        double bucketSize)
    {
        var bucket = Math.Max(bucketSize, 0.5);
        return string.Join(
            ":",
            endpointWallId,
            hostWallId,
            Math.Round(point.X / bucket).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Math.Round(point.Y / bucket).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static int AddCollinearWallJunctions(
        WallSegment first,
        WallSegment second,
        double alignmentTolerance,
        double duplicateTolerance,
        IReadOnlyDictionary<string, List<PlanPoint>> pointsByWallId)
    {
        if (!IsNearParallel(first.CenterLine, second.CenterLine)
            || !AreNearCollinear(first.CenterLine, second.CenterLine, alignmentTolerance))
        {
            return 0;
        }

        var candidates = new List<PlanPoint>();
        AddCollinearEndpointProjection(first.CenterLine.Start, second.CenterLine, alignmentTolerance, duplicateTolerance, candidates);
        AddCollinearEndpointProjection(first.CenterLine.End, second.CenterLine, alignmentTolerance, duplicateTolerance, candidates);
        AddCollinearEndpointProjection(second.CenterLine.Start, first.CenterLine, alignmentTolerance, duplicateTolerance, candidates);
        AddCollinearEndpointProjection(second.CenterLine.End, first.CenterLine, alignmentTolerance, duplicateTolerance, candidates);

        var added = 0;
        foreach (var candidate in candidates)
        {
            var addedToFirst = AddPointIfMissing(pointsByWallId[first.Id], candidate, duplicateTolerance);
            var addedToSecond = AddPointIfMissing(pointsByWallId[second.Id], candidate, duplicateTolerance);
            if (addedToFirst || addedToSecond)
            {
                added++;
            }
        }

        return added;
    }

    private static bool CanUsePairForAutomaticJunctions(
        WallSegment first,
        WallSegment second,
        IReadOnlySet<string> automaticCoordinateRepairWallIds) =>
        automaticCoordinateRepairWallIds.Contains(first.Id)
        && automaticCoordinateRepairWallIds.Contains(second.Id);

    private static bool WouldCreateAutomaticJunction(
        WallSegment first,
        WallSegment second,
        double nearTouchTolerance,
        ScannerOptions options) =>
        GeometryOperations.TryIntersect(
            first.CenterLine,
            second.CenterLine,
            options.WallSnapTolerance,
            out _)
        || InferNearTouchJunctions(
            first.CenterLine,
            second.CenterLine,
            nearTouchTolerance,
            options.WallSnapTolerance).Count > 0
        || CanCreateCollinearJunction(first, second, options.WallSnapTolerance);

    private static bool CanCreateCollinearJunction(WallSegment first, WallSegment second, double alignmentTolerance) =>
        IsNearParallel(first.CenterLine, second.CenterLine)
        && AreNearCollinear(first.CenterLine, second.CenterLine, alignmentTolerance)
        && (CanProjectEndpointToSegment(first.CenterLine.Start, second.CenterLine, alignmentTolerance)
            || CanProjectEndpointToSegment(first.CenterLine.End, second.CenterLine, alignmentTolerance)
            || CanProjectEndpointToSegment(second.CenterLine.Start, first.CenterLine, alignmentTolerance)
            || CanProjectEndpointToSegment(second.CenterLine.End, first.CenterLine, alignmentTolerance));

    private static bool CanProjectEndpointToSegment(
        PlanPoint endpoint,
        PlanLineSegment host,
        double alignmentTolerance)
    {
        var hostLength = Math.Max(host.Length, 1);
        var parameterTolerance = alignmentTolerance / hostLength;
        var parameter = host.ProjectParameter(endpoint);
        if (parameter < -parameterTolerance || parameter > 1 + parameterTolerance)
        {
            return false;
        }

        return endpoint.DistanceTo(host.PointAt(Math.Clamp(parameter, 0, 1))) <= alignmentTolerance;
    }

    private static bool IsNearParallel(PlanLineSegment first, PlanLineSegment second)
    {
        var delta = Math.Abs(
            GeometryOperations.NormalizeAngleRadians(first.AngleRadians)
            - GeometryOperations.NormalizeAngleRadians(second.AngleRadians));
        delta = Math.Min(delta, Math.PI - delta);
        return delta <= 0.08;
    }

    private static bool AreNearCollinear(PlanLineSegment first, PlanLineSegment second, double tolerance) =>
        first.DistanceToPoint(second.Start) <= tolerance
        || first.DistanceToPoint(second.End) <= tolerance
        || second.DistanceToPoint(first.Start) <= tolerance
        || second.DistanceToPoint(first.End) <= tolerance;

    private static void AddCollinearEndpointProjection(
        PlanPoint endpoint,
        PlanLineSegment host,
        double alignmentTolerance,
        double duplicateTolerance,
        List<PlanPoint> candidates)
    {
        var hostLength = Math.Max(host.Length, 1);
        var parameterTolerance = alignmentTolerance / hostLength;
        var parameter = host.ProjectParameter(endpoint);
        if (parameter < -parameterTolerance || parameter > 1 + parameterTolerance)
        {
            return;
        }

        var projected = host.PointAt(Math.Clamp(parameter, 0, 1));
        if (endpoint.DistanceTo(projected) > alignmentTolerance)
        {
            return;
        }

        AddPointIfMissing(candidates, projected, duplicateTolerance);
    }

    private static bool AddPointIfMissing(List<PlanPoint> points, PlanPoint point, double duplicateTolerance)
    {
        if (points.Any(existing => existing.DistanceTo(point) <= duplicateTolerance))
        {
            return false;
        }

        points.Add(point);
        return true;
    }

    private static List<PlanPoint> TrimEndpointOverruns(
        List<PlanPoint> orderedPoints,
        WallSegment wall,
        IReadOnlyDictionary<string, List<PlanPoint>> pointsByWallId,
        IReadOnlyList<WallSegment> pageWalls,
        ScannerOptions options,
        out int trimmedCount,
        out IReadOnlyList<EndpointOverrunReview> overrunReviews)
    {
        trimmedCount = 0;
        overrunReviews = Array.Empty<EndpointOverrunReview>();
        if (orderedPoints.Count < 3)
        {
            return orderedPoints;
        }

        var normalized = new List<PlanPoint>(orderedPoints);
        var reviews = new List<EndpointOverrunReview>();
        var trimTolerance = EndpointOverrunTrimTolerance(options);
        var sharedTolerance = Math.Max(options.WallSnapTolerance, 0.5);

        if (normalized.Count >= 3
            && ShouldTrimEndpointTail(
                wall,
                normalized[0],
                normalized[1],
                trimTolerance,
                pointsByWallId,
                pageWalls,
                options,
                sharedTolerance))
        {
            normalized.RemoveAt(0);
            trimmedCount++;
        }
        else if (normalized.Count >= 3
            && TryCreateEndpointOverrunReview(
                wall,
                normalized[0],
                normalized[1],
                pointsByWallId,
                pageWalls,
                options,
                sharedTolerance,
                out var review))
        {
            reviews.Add(review);
        }

        if (normalized.Count >= 3
            && ShouldTrimEndpointTail(
                wall,
                normalized[^1],
                normalized[^2],
                trimTolerance,
                pointsByWallId,
                pageWalls,
                options,
                sharedTolerance))
        {
            normalized.RemoveAt(normalized.Count - 1);
            trimmedCount++;
        }
        else if (normalized.Count >= 3
            && TryCreateEndpointOverrunReview(
                wall,
                normalized[^1],
                normalized[^2],
                pointsByWallId,
                pageWalls,
                options,
                sharedTolerance,
                out var review))
        {
            reviews.Add(review);
        }

        overrunReviews = reviews.ToArray();
        return normalized;
    }

    private static List<PlanPoint> SnapNearTouchEndpointGaps(
        List<PlanPoint> orderedPoints,
        WallSegment wall,
        IReadOnlyDictionary<string, List<PlanPoint>> pointsByWallId,
        IReadOnlyList<WallSegment> pageWalls,
        IReadOnlyDictionary<string, IReadOnlyList<PlanPoint>> pairedEndpointSnapPointsByWallId,
        ScannerOptions options,
        out int snappedCount)
    {
        snappedCount = 0;
        if (orderedPoints.Count < 3)
        {
            return orderedPoints;
        }

        var normalized = new List<PlanPoint>(orderedPoints);
        var snapTolerance = InferredNearTouchJunctionTolerance(options);
        var sharedTolerance = Math.Max(options.WallSnapTolerance, 0.5);

        if (normalized.Count >= 3
            && ShouldTrimApprovedEndpointSnapTail(
                wall,
                normalized[0],
                normalized[1],
                pairedEndpointSnapPointsByWallId,
                options,
                sharedTolerance))
        {
            normalized.RemoveAt(0);
            snappedCount++;
        }
        else if (normalized.Count >= 3
            && ShouldSnapNearTouchEndpointGap(
                wall,
                normalized[0],
                normalized[1],
                pointsByWallId,
                pageWalls,
                pairedEndpointSnapPointsByWallId,
                snapTolerance,
                PairedEndpointSnapTolerance(options),
                sharedTolerance))
        {
            normalized.RemoveAt(1);
            snappedCount++;
        }

        if (normalized.Count >= 3
            && ShouldTrimApprovedEndpointSnapTail(
                wall,
                normalized[^1],
                normalized[^2],
                pairedEndpointSnapPointsByWallId,
                options,
                sharedTolerance))
        {
            normalized.RemoveAt(normalized.Count - 1);
            snappedCount++;
        }
        else if (normalized.Count >= 3
            && ShouldSnapNearTouchEndpointGap(
                wall,
                normalized[^1],
                normalized[^2],
                pointsByWallId,
                pageWalls,
                pairedEndpointSnapPointsByWallId,
                snapTolerance,
                PairedEndpointSnapTolerance(options),
                sharedTolerance))
        {
            normalized.RemoveAt(normalized.Count - 2);
            snappedCount++;
        }

        return normalized;
    }

    private static bool ShouldTrimApprovedEndpointSnapTail(
        WallSegment wall,
        PlanPoint endpoint,
        PlanPoint junctionPoint,
        IReadOnlyDictionary<string, IReadOnlyList<PlanPoint>> pairedEndpointSnapPointsByWallId,
        ScannerOptions options,
        double sharedTolerance)
    {
        var tailLength = endpoint.DistanceTo(junctionPoint);
        if (tailLength <= 0.001 || tailLength > EndpointOverrunTrimTolerance(options))
        {
            return false;
        }

        var endpointTolerance = Math.Max(sharedTolerance, 0.5);
        if (endpoint.DistanceTo(wall.CenterLine.Start) > endpointTolerance
            && endpoint.DistanceTo(wall.CenterLine.End) > endpointTolerance)
        {
            return false;
        }

        return IsApprovedPairedEndpointSnap(
            wall.Id,
            junctionPoint,
            pairedEndpointSnapPointsByWallId,
            sharedTolerance);
    }

    private static bool IsReviewedEndpointOverrunTail(
        PlanPoint first,
        PlanPoint second,
        IReadOnlyList<EndpointOverrunReview> reviews,
        ScannerOptions options)
    {
        if (reviews.Count == 0)
        {
            return false;
        }

        var tolerance = Math.Max(options.WallSnapTolerance, 0.5);
        return reviews.Any(review =>
            (first.DistanceTo(review.Endpoint) <= tolerance && second.DistanceTo(review.JunctionPoint) <= tolerance)
            || (second.DistanceTo(review.Endpoint) <= tolerance && first.DistanceTo(review.JunctionPoint) <= tolerance));
    }

    private static WallSegment NormalizeWallSegmentCenterLine(
        WallSegment wall,
        IReadOnlyList<PlanPoint> orderedPoints,
        PlanCalibration calibration,
        int trimmedEndpointCount,
        int snappedEndpointGapCount)
    {
        if (orderedPoints.Count < 2 || trimmedEndpointCount <= 0 && snappedEndpointGapCount <= 0)
        {
            return wall;
        }

        var normalizedLine = CreateAxisPreservingNormalizedLine(wall.CenterLine, orderedPoints[0], orderedPoints[^1]);
        if (normalizedLine.Length <= 1
            || wall.CenterLine.Start.DistanceTo(normalizedLine.Start) <= 0.001
            && wall.CenterLine.End.DistanceTo(normalizedLine.End) <= 0.001)
        {
            return wall;
        }

        var scaleGroup = calibration.SelectMeasurementScaleGroup(
            wall.PageNumber,
            normalizedLine.Bounds.Inflate(Math.Max(wall.Thickness / 2.0, 0.5)),
            wall.SourceRegionId);
        var evidence = wall.Evidence
            .Concat(WallNormalizationEvidence(trimmedEndpointCount, snappedEndpointGapCount))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return wall with
        {
            CenterLine = normalizedLine,
            Evidence = evidence,
            LengthMeters = calibration.ToMeters(normalizedLine.Length, scaleGroup) ?? wall.LengthMeters,
            ThicknessMillimeters = calibration.ToMillimeters(wall.Thickness, scaleGroup) ?? wall.ThicknessMillimeters,
            MeasurementScaleGroupId = scaleGroup?.Id ?? wall.MeasurementScaleGroupId
        };
    }

    internal static PlanLineSegment CreateAxisPreservingNormalizedLine(
        PlanLineSegment sourceLine,
        PlanPoint start,
        PlanPoint end)
    {
        var startParameter = sourceLine.ProjectParameter(start);
        var endParameter = sourceLine.ProjectParameter(end);
        return new PlanLineSegment(
            sourceLine.PointAt(startParameter),
            sourceLine.PointAt(endParameter));
    }

    private static IEnumerable<string> WallNormalizationEvidence(
        int trimmedEndpointCount,
        int snappedEndpointGapCount)
    {
        if (snappedEndpointGapCount > 0)
        {
            yield return $"snapped {snappedEndpointGapCount} near-touch endpoint gap(s) into wall centerline";
        }

        if (trimmedEndpointCount > 0)
        {
            yield return $"trimmed {trimmedEndpointCount} supported endpoint overrun(s) from wall centerline";
        }
    }

    private static bool ShouldSnapNearTouchEndpointGap(
        WallSegment wall,
        PlanPoint inferredJunctionPoint,
        PlanPoint originalEndpoint,
        IReadOnlyDictionary<string, List<PlanPoint>> pointsByWallId,
        IReadOnlyList<WallSegment> pageWalls,
        IReadOnlyDictionary<string, IReadOnlyList<PlanPoint>> pairedEndpointSnapPointsByWallId,
        double snapTolerance,
        double pairedEndpointSnapTolerance,
        double sharedTolerance)
    {
        var gap = inferredJunctionPoint.DistanceTo(originalEndpoint);
        if (gap <= 0.001)
        {
            return false;
        }

        var hasPairedEndpointSnapSupport = IsApprovedPairedEndpointSnap(
            wall.Id,
            inferredJunctionPoint,
            pairedEndpointSnapPointsByWallId,
            sharedTolerance);
        if (gap > snapTolerance
            && (!hasPairedEndpointSnapSupport || gap > pairedEndpointSnapTolerance))
        {
            return false;
        }

        var endpointTolerance = Math.Max(sharedTolerance, 0.5);
        if (originalEndpoint.DistanceTo(wall.CenterLine.Start) > endpointTolerance
            && originalEndpoint.DistanceTo(wall.CenterLine.End) > endpointTolerance)
        {
            return false;
        }

        if (DistanceToInfiniteLine(wall.CenterLine, inferredJunctionPoint) > Math.Max(1, sharedTolerance))
        {
            return false;
        }

        var parameter = wall.CenterLine.ProjectParameter(inferredJunctionPoint);
        const double parameterTolerance = 0.001;
        if (parameter >= -parameterTolerance && parameter <= 1 + parameterTolerance)
        {
            return false;
        }

        if (!IsSharedJunctionPoint(wall.Id, inferredJunctionPoint, pointsByWallId, pageWalls, sharedTolerance)
            || IsSharedJunctionPoint(wall.Id, originalEndpoint, pointsByWallId, pageWalls, sharedTolerance))
        {
            return false;
        }

        return true;
    }

    private static double DistanceToInfiniteLine(PlanLineSegment line, PlanPoint point) =>
        line.PointAt(line.ProjectParameter(point)).DistanceTo(point);

    private static bool IsSharedJunctionPoint(
        string wallId,
        PlanPoint point,
        IReadOnlyDictionary<string, List<PlanPoint>> pointsByWallId,
        IReadOnlyList<WallSegment> pageWalls,
        double tolerance) =>
        pageWalls
            .Where(wall => !string.Equals(wall.Id, wallId, StringComparison.Ordinal))
            .Any(wall => pointsByWallId[wall.Id].Any(existing => existing.DistanceTo(point) <= tolerance));

    private static bool ShouldTrimEndpointTail(
        WallSegment wall,
        PlanPoint endpoint,
        PlanPoint junctionPoint,
        double trimTolerance,
        IReadOnlyDictionary<string, List<PlanPoint>> pointsByWallId,
        IReadOnlyList<WallSegment> pageWalls,
        ScannerOptions options,
        double sharedTolerance)
    {
        var tailLength = endpoint.DistanceTo(junctionPoint);
        if (tailLength <= 0.001)
        {
            return false;
        }

        if (IsSharedJunctionPoint(wall.Id, endpoint, pointsByWallId, pageWalls, sharedTolerance))
        {
            return false;
        }

        var support = EndpointTrimSupportAt(wall, junctionPoint, pointsByWallId, pageWalls, sharedTolerance);
        if (!support.HasSharedJunction)
        {
            return false;
        }

        if (tailLength <= trimTolerance)
        {
            return true;
        }

        return support.HasPerpendicularEndpointJunction
            && tailLength <= ExtendedEndpointOverrunTrimTolerance(options)
            && tailLength <= Math.Max(wall.DrawingLength * 0.35, trimTolerance);
    }

    private static bool TryCreateEndpointOverrunReview(
        WallSegment wall,
        PlanPoint endpoint,
        PlanPoint junctionPoint,
        IReadOnlyDictionary<string, List<PlanPoint>> pointsByWallId,
        IReadOnlyList<WallSegment> pageWalls,
        ScannerOptions options,
        double sharedTolerance,
        out EndpointOverrunReview review)
    {
        review = default!;
        var tailLength = endpoint.DistanceTo(junctionPoint);
        if (tailLength <= 0.001)
        {
            return false;
        }

        if (IsSharedJunctionPoint(wall.Id, endpoint, pointsByWallId, pageWalls, sharedTolerance))
        {
            return false;
        }

        var support = EndpointTrimSupportAt(wall, junctionPoint, pointsByWallId, pageWalls, sharedTolerance);
        var autoTrimLimit = ExtendedEndpointOverrunTrimTolerance(options);
        var reviewLimit = EndpointOverrunReviewTolerance(options);
        if (!support.HasPerpendicularEndpointJunction
            || tailLength <= autoTrimLimit
            || tailLength > reviewLimit
            || tailLength > Math.Max(wall.DrawingLength * 0.45, autoTrimLimit))
        {
            return false;
        }

        review = new EndpointOverrunReview(
            wall.PageNumber,
            wall.Id,
            endpoint,
            junctionPoint,
            tailLength,
            autoTrimLimit,
            reviewLimit,
            support.WallIds);
        return true;
    }

    private static EndpointTrimSupport EndpointTrimSupportAt(
        WallSegment wall,
        PlanPoint junctionPoint,
        IReadOnlyDictionary<string, List<PlanPoint>> pointsByWallId,
        IReadOnlyList<WallSegment> pageWalls,
        double tolerance)
    {
        var wallIds = new List<string>();
        var perpendicularWallIds = new List<string>();
        var perpendicularEndpointWallIds = new List<string>();

        foreach (var other in pageWalls.Where(other => !string.Equals(other.Id, wall.Id, StringComparison.Ordinal)))
        {
            if (!pointsByWallId[other.Id].Any(existing => existing.DistanceTo(junctionPoint) <= tolerance))
            {
                continue;
            }

            wallIds.Add(other.Id);
            if (IsNearPerpendicular(wall.CenterLine, other.CenterLine))
            {
                perpendicularWallIds.Add(other.Id);
                if (other.CenterLine.Start.DistanceTo(junctionPoint) <= tolerance
                    || other.CenterLine.End.DistanceTo(junctionPoint) <= tolerance)
                {
                    perpendicularEndpointWallIds.Add(other.Id);
                }
            }
        }

        return new EndpointTrimSupport(
            wallIds.Count > 0,
            perpendicularWallIds.Count > 0,
            perpendicularEndpointWallIds.Count > 0,
            wallIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            perpendicularWallIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            perpendicularEndpointWallIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static double EndpointOverrunTrimTolerance(ScannerOptions options) =>
        Math.Max(
            InferredNearTouchJunctionTolerance(options),
            Math.Min(
                UnresolvedEndpointGapReviewTolerance(options),
                Math.Max(options.DefaultWallThickness * 4.5, options.MaxWallFragmentGap * 2.5)));

    private static double ExtendedEndpointOverrunTrimTolerance(ScannerOptions options) =>
        Math.Max(
            EndpointOverrunTrimTolerance(options),
            Math.Min(
                Math.Max(options.MaxOpeningGap * 0.8, options.DefaultWallThickness * 8.0),
                Math.Max(options.DefaultWallThickness * 12.0, options.MaxWallFragmentGap * 7.0)));

    private static double EndpointOverrunReviewTolerance(ScannerOptions options) =>
        Math.Max(
            ExtendedEndpointOverrunTrimTolerance(options),
            Math.Min(
                Math.Max(options.MaxOpeningGap * 1.8, options.DefaultWallThickness * 20.0),
                Math.Max(options.DefaultWallThickness * 30.0, options.MaxWallFragmentGap * 12.0)));

    private static IReadOnlyList<PlanPoint> InferNearTouchJunctions(
        PlanLineSegment first,
        PlanLineSegment second,
        double tolerance,
        double duplicateTolerance)
    {
        if (!IsNearPerpendicular(first, second))
        {
            return Array.Empty<PlanPoint>();
        }

        var points = new List<PlanPoint>();
        AddProjectedEndpointJunction(first.Start, second, tolerance, duplicateTolerance, points);
        AddProjectedEndpointJunction(first.End, second, tolerance, duplicateTolerance, points);
        AddProjectedEndpointJunction(second.Start, first, tolerance, duplicateTolerance, points);
        AddProjectedEndpointJunction(second.End, first, tolerance, duplicateTolerance, points);
        return points;
    }

    private static void AddProjectedEndpointJunction(
        PlanPoint endpoint,
        PlanLineSegment host,
        double tolerance,
        double duplicateTolerance,
        List<PlanPoint> points)
    {
        var hostLength = Math.Max(host.Length, 1);
        var parameterTolerance = tolerance / hostLength;
        var parameter = host.ProjectParameter(endpoint);
        if (parameter < -parameterTolerance || parameter > 1 + parameterTolerance)
        {
            return;
        }

        var projected = host.PointAt(Math.Clamp(parameter, 0, 1));
        if (endpoint.DistanceTo(projected) > tolerance)
        {
            return;
        }

        if (points.Any(existing => existing.DistanceTo(projected) <= duplicateTolerance))
        {
            return;
        }

        points.Add(projected);
    }

    private static bool IsNearPerpendicular(PlanLineSegment first, PlanLineSegment second)
    {
        var delta = Math.Abs(
            GeometryOperations.NormalizeAngleRadians(first.AngleRadians)
            - GeometryOperations.NormalizeAngleRadians(second.AngleRadians));
        delta = Math.Min(delta, Math.PI - delta);
        return Math.Abs(delta - (Math.PI / 2.0)) <= 0.20;
    }

    private static double InferredNearTouchJunctionTolerance(ScannerOptions options) =>
        Math.Max(
            options.WallSnapTolerance,
            Math.Min(
                Math.Max(options.MaxWallFragmentGap, options.WallSnapTolerance) + options.WallSnapTolerance,
                Math.Max(options.WallSnapTolerance * 3.0, options.DefaultWallThickness * 2.0)));

    private static NodeClassification ClassifyNode(NodeAccumulator node)
    {
        var directions = node.IncidentDirections
            .Select(DirectionName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(direction => DirectionSortOrder(direction))
            .ToArray();
        var evidence = new List<string>
        {
            $"degree {node.Degree}"
        };

        if (directions.Length > 0)
        {
            evidence.Add($"directions {string.Join(", ", directions)}");
        }

        if (node.PositionObservationCount > 1)
        {
            evidence.Add($"position resolved from {node.PositionObservationCount} snapped observations");
            if (node.PositionObservationSpread > 0.001)
            {
                evidence.Add(
                    "snap observation spread "
                    + node.PositionObservationSpread.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + " drawing units");
            }
        }

        var kind = node.Degree switch
        {
            <= 1 => WallNodeKind.Endpoint,
            2 when HasOppositePair(node.IncidentDirections) => WallNodeKind.Inline,
            2 when HasPerpendicularPair(node.IncidentDirections) => WallNodeKind.Corner,
            3 when HasOppositePair(node.IncidentDirections) && HasPerpendicularPair(node.IncidentDirections) => WallNodeKind.TJunction,
            >= 4 when HasHorizontalOpposite(node.IncidentDirections) && HasVerticalOpposite(node.IncidentDirections) => WallNodeKind.Crossing,
            _ => WallNodeKind.Junction
        };

        evidence.Add($"classified {kind}");
        return new NodeClassification(kind, directions, evidence);
    }

    private static bool HasOppositePair(IEnumerable<DirectionBucket> directions) =>
        HasHorizontalOpposite(directions) || HasVerticalOpposite(directions);

    private static bool HasHorizontalOpposite(IEnumerable<DirectionBucket> directions)
    {
        var set = directions.ToHashSet();
        return set.Contains(DirectionBucket.East) && set.Contains(DirectionBucket.West);
    }

    private static bool HasVerticalOpposite(IEnumerable<DirectionBucket> directions)
    {
        var set = directions.ToHashSet();
        return set.Contains(DirectionBucket.North) && set.Contains(DirectionBucket.South);
    }

    private static bool HasPerpendicularPair(IEnumerable<DirectionBucket> directions)
    {
        var set = directions.ToHashSet();
        var hasHorizontal = set.Contains(DirectionBucket.East) || set.Contains(DirectionBucket.West);
        var hasVertical = set.Contains(DirectionBucket.North) || set.Contains(DirectionBucket.South);
        return hasHorizontal && hasVertical;
    }

    private static string DirectionName(DirectionBucket direction) =>
        direction.ToString();

    private static int DirectionSortOrder(string direction) =>
        direction switch
        {
            nameof(DirectionBucket.North) => 0,
            nameof(DirectionBucket.East) => 1,
            nameof(DirectionBucket.South) => 2,
            nameof(DirectionBucket.West) => 3,
            _ => 4
        };

    private static NodeAccumulator GetOrCreateNode(
        List<NodeAccumulator> nodes,
        int pageNumber,
        PlanPoint point,
        ScannerOptions options)
    {
        var existing = nodes.FirstOrDefault(node =>
            node.PageNumber == pageNumber
            && node.Position.DistanceTo(point) <= options.WallSnapTolerance);

        if (existing is not null)
        {
            existing.AddPositionObservation(point);
            return existing;
        }

        var node = new NodeAccumulator(
            $"page:{pageNumber}:node:{nodes.Count + 1}",
            pageNumber,
            point);

        nodes.Add(node);
        return node;
    }

    private sealed class NodeAccumulator
    {
        public NodeAccumulator(string id, int pageNumber, PlanPoint position)
        {
            Id = id;
            PageNumber = pageNumber;
            Position = position;
            _positionObservations.Add(position);
        }

        private readonly List<PlanPoint> _positionObservations = new();

        public string Id { get; }

        public int PageNumber { get; }

        public PlanPoint Position { get; set; }

        public int PositionObservationCount => _positionObservations.Count;

        public double PositionObservationSpread => _positionObservations.Count <= 1
            ? 0
            : _positionObservations.Max(point => point.DistanceTo(Position));

        public List<DirectionBucket> IncidentDirections { get; } = new();

        public int Degree => IncidentDirections.Count;

        public void AddPositionObservation(PlanPoint point)
        {
            _positionObservations.Add(point);
            Position = new PlanPoint(
                DominantCoordinateOrMedian(_positionObservations.Select(item => item.X)),
                DominantCoordinateOrMedian(_positionObservations.Select(item => item.Y)));
        }

        public void AddIncidentDirection(PlanPoint from, PlanPoint to)
        {
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            if (Math.Abs(dx) <= double.Epsilon && Math.Abs(dy) <= double.Epsilon)
            {
                IncidentDirections.Add(DirectionBucket.Other);
                return;
            }

            if (Math.Abs(dx) >= Math.Abs(dy))
            {
                IncidentDirections.Add(dx >= 0 ? DirectionBucket.East : DirectionBucket.West);
            }
            else
            {
                IncidentDirections.Add(dy >= 0 ? DirectionBucket.South : DirectionBucket.North);
            }
        }

        private static double DominantCoordinateOrMedian(IEnumerable<double> coordinates)
        {
            const double coordinateMatchTolerance = 0.001;

            var sorted = coordinates.Order().ToArray();
            if (sorted.Length == 0)
            {
                return 0;
            }

            var median = Median(sorted);
            var groups = new List<CoordinateGroup>();
            foreach (var coordinate in sorted)
            {
                if (groups.Count == 0 || Math.Abs(coordinate - groups[^1].Last) > coordinateMatchTolerance)
                {
                    groups.Add(new CoordinateGroup(coordinate));
                }
                else
                {
                    groups[^1].Add(coordinate);
                }
            }

            var dominant = groups
                .OrderByDescending(group => group.Count)
                .ThenBy(group => Math.Abs(group.Center - median))
                .ThenBy(group => group.Center)
                .First();

            return dominant.Count > 1 ? dominant.Center : median;
        }

        private static double Median(IReadOnlyList<double> sorted)
        {
            var middle = sorted.Count / 2;
            return sorted.Count % 2 == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) / 2.0;
        }
    }

    private sealed class CoordinateGroup
    {
        private double _sum;

        public CoordinateGroup(double coordinate)
        {
            Last = coordinate;
            _sum = coordinate;
            Count = 1;
        }

        public double Last { get; private set; }

        public int Count { get; private set; }

        public double Center => _sum / Count;

        public void Add(double coordinate)
        {
            Last = coordinate;
            _sum += coordinate;
            Count++;
        }
    }

    private sealed record NodeClassification(
        WallNodeKind Kind,
        IReadOnlyList<string> Directions,
        IReadOnlyList<string> Evidence);

    private sealed record PairedEndpointSnapCandidate(
        WallSegment EndpointWall,
        WallSegment HostWall,
        PlanPoint Endpoint,
        PlanPoint JunctionPoint,
        double GapDistance);

    private sealed record PairedEndpointSnap(
        string EndpointWallId,
        string HostWallId,
        PlanPoint Endpoint,
        PlanPoint JunctionPoint,
        double GapDistance,
        IReadOnlyList<string> SupportWallIds);

    private readonly record struct EndpointTrimSupport(
        bool HasSharedJunction,
        bool HasPerpendicularJunction,
        bool HasPerpendicularEndpointJunction,
        IReadOnlyList<string> WallIds,
        IReadOnlyList<string> PerpendicularWallIds,
        IReadOnlyList<string> PerpendicularEndpointWallIds);

    private sealed record EndpointOverrunReview(
        int PageNumber,
        string WallId,
        PlanPoint Endpoint,
        PlanPoint JunctionPoint,
        double TailLength,
        double AutoTrimLimit,
        double ReviewLimit,
        IReadOnlyList<string> SupportWallIds);

    private sealed record RawWallGraphComponent(
        int PageNumber,
        IReadOnlyList<string> WallIds,
        IReadOnlyList<string> NodeIds,
        IReadOnlyList<string> EdgeIds,
        IReadOnlyList<string> SourcePrimitiveIds,
        PlanRect Bounds,
        double DrawingLength,
        int HorizontalWallCount,
        int VerticalWallCount,
        int DiagonalWallCount,
        int ShortWallCount,
        int SingleLineWallCount,
        int PairedWallCount,
        int FragmentMergedWallCount,
        int InteriorWallCount,
        int ExteriorWallCount);

    private enum OrthogonalAxis
    {
        Horizontal,
        Vertical
    }

    private enum DirectionBucket
    {
        North,
        East,
        South,
        West,
        Other
    }
}
