namespace OpenPlanTrace;

internal sealed class WallTypeRefinementStage : IPipelineStage
{
    private const string StageName = "wall-type-refinement";
    private const double MinTrustedDimensionLikeDenseRoomBoundaryPairScore = 0.80;
    private const double MinSecondaryTrustedDimensionLikeDenseRoomBoundaryLength = 32.0;
    private const int MaxTrustedDimensionLikeDenseRoomBoundaryFaceFragments = 32;
    private const int MaxTrustedDimensionLikeDenseRoomBoundaryTotalFaceFragments = 48;

    public string Name => StageName;

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        if (context.Walls.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        var roomWallReferences = RoomBoundaryWallReferenceBuilder.Build(
            context.Rooms,
            context.Walls,
            context.Options.WallSnapTolerance);
        var roomIdsByWallId = roomWallReferences.RoomIdsByWallId;
        var sharedWallIds = BuildSharedWallIds(context.RoomAdjacencyGraph);
        var componentsByWallId = BuildComponentsByWallId(context.WallGraph);
        var supportedTopologyEndpointCountsByWallId = BuildSupportedTopologyEndpointCounts(context.WallGraph);
        var evidenceByWallId = BuildEvidenceByWallId(context.WallEvidenceMap);
        var rejectedEvidenceByWallId = BuildRejectedEvidenceByWallId(context.WallEvidenceMap);
        var exteriorContinuitySupportedWallIds = BuildExteriorContinuitySupportedWallIds(
            context.Walls,
            evidenceByWallId,
            context.Options);
        var roomsById = context.Rooms
            .Where(room => !string.IsNullOrWhiteSpace(room.Id))
            .GroupBy(room => room.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var roomsByPage = context.Rooms
            .GroupBy(room => room.PageNumber)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var mainFloorplanBoundsByPage = context.SheetRegions
            .Where(region => region.Kind == RegionKind.MainFloorPlan)
            .GroupBy(region => region.PageNumber)
            .ToDictionary(
                group => group.Key,
                group => PlanRect.Union(group.Select(region => region.Bounds)));
        var changed = 0;
        var evidenceUpdated = 0;
        var roomReferenced = 0;
        var twoSidedRoomEvidence = 0;
        var oneSidedRoomEvidence = 0;
        var rejectedEvidenceProtected = 0;
        var roomConfirmedPlacementPromoted = 0;
        var topologySupportedFragmentedPairPromoted = 0;
        var fragmentedPairPlacementDemoted = 0;
        var denseLocalDetailPlacementDemoted = 0;
        var nonOrthogonalDimensionLikePlacementDemoted = 0;
        var shortDimensionLikePlacementDemoted = 0;
        var fragmentedExteriorShellContinuityRetained = 0;
        var geometricRoomBoundaryEvidenceAdded = 0;
        var explicitRoomBoundaryEvidenceAdded = 0;
        var updatedAssessmentsByWallId = new Dictionary<string, WallEvidenceWallAssessment>(StringComparer.Ordinal);

        for (var index = 0; index < context.Walls.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var wall = context.Walls[index];
            var wallRoomIds = roomIdsByWallId.TryGetValue(wall.Id, out var ids)
                ? ids
                : Array.Empty<string>();
            if (wallRoomIds.Length > 0)
            {
                roomReferenced++;
            }

            var sideEvidence = roomsByPage.TryGetValue(wall.PageNumber, out var pageRooms)
                ? AnalyzeRoomSides(wall, pageRooms, context.Options)
                : RoomSideEvidence.Empty;
            if (sideEvidence.PositiveRoomHits > 0 && sideEvidence.NegativeRoomHits > 0)
            {
                twoSidedRoomEvidence++;
            }
            else if (sideEvidence.PositiveRoomHits > 0 || sideEvidence.NegativeRoomHits > 0)
            {
                oneSidedRoomEvidence++;
            }

            var component = componentsByWallId.TryGetValue(wall.Id, out var foundComponent)
                ? foundComponent
                : null;
            var rejectedEvidence = rejectedEvidenceByWallId.TryGetValue(wall.Id, out var foundEvidence)
                ? foundEvidence
                : null;
            if (rejectedEvidence is not null)
            {
                rejectedEvidenceProtected++;
            }

            var hasOutdoorRoomReference = wallRoomIds
                .Select(id => roomsById.TryGetValue(id, out var room) ? room : null)
                .OfType<RoomRegion>()
                .Any(room => room.UseKind == RoomUseKind.Outdoor);
            var refined = RefineWallType(
                wall,
                evidenceByWallId.TryGetValue(wall.Id, out var wallAssessment) ? wallAssessment : null,
                wallRoomIds.Length,
                sharedWallIds.Contains(wall.Id),
                hasOutdoorRoomReference,
                sideEvidence,
                component,
                rejectedEvidence,
                mainFloorplanBoundsByPage.TryGetValue(wall.PageNumber, out var mainFloorplanBounds)
                    ? mainFloorplanBounds
                    : null,
                context.Options);

            var evidence = IsActionableEvidence(refined.Evidence)
                ? AppendEvidence(wall.Evidence, refined.Evidence)
                : wall.Evidence;
            var evidenceChanged = evidence.Count != wall.Evidence.Count;
            var updatedWall = wall;

            if (refined.WallType != wall.WallType || evidenceChanged)
            {
                updatedWall = wall with
                {
                    WallType = refined.WallType,
                    Evidence = evidence
                };

                if (refined.WallType != wall.WallType)
                {
                    changed++;
                }

                if (evidenceChanged)
                {
                    evidenceUpdated++;
                }
            }

            evidenceByWallId.TryGetValue(wall.Id, out var assessment);
            var hasExteriorShellContinuitySupport = exteriorContinuitySupportedWallIds.Contains(wall.Id);
            var hasGeometricRoomBoundarySupport = roomWallReferences.GeometricRoomBoundaryWallIds.Contains(wall.Id);
            if (assessment is not null
                && TryDemoteFragmentedPlacementReadyWallEvidence(
                    updatedWall,
                    assessment,
                    context.Options,
                    component,
                    wallRoomIds.Length,
                    sharedWallIds.Contains(wall.Id),
                    sideEvidence,
                    supportedTopologyEndpointCountsByWallId.TryGetValue(wall.Id, out var demotionSupportedEndpointCount)
                        ? demotionSupportedEndpointCount
                        : 0,
                    hasExteriorShellContinuitySupport,
                    out var demotedAssessment,
                    out var demotionEvidence))
            {
                updatedAssessmentsByWallId[wall.Id] = demotedAssessment;
                updatedWall = updatedWall with
                {
                    Evidence = AppendEvidence(updatedWall.Evidence, demotionEvidence)
                };
                fragmentedPairPlacementDemoted++;
                evidenceUpdated++;
                assessment = demotedAssessment;
            }
            else if (assessment is not null
                && TryDemoteDenseLocalDetailPlacementReadyWallEvidence(
                    updatedWall,
                    assessment,
                    context.Options,
                    context.Walls,
                    component,
                    wallRoomIds.Length,
                    sharedWallIds.Contains(wall.Id),
                    sideEvidence,
                    supportedTopologyEndpointCountsByWallId.TryGetValue(wall.Id, out var denseDetailSupportedEndpointCount)
                        ? denseDetailSupportedEndpointCount
                        : 0,
                    hasGeometricRoomBoundarySupport,
                    hasExteriorShellContinuitySupport,
                    out var denseDetailDemotedAssessment,
                    out var denseDetailDemotionEvidence))
            {
                updatedAssessmentsByWallId[wall.Id] = denseDetailDemotedAssessment;
                updatedWall = updatedWall with
                {
                    Evidence = AppendEvidence(updatedWall.Evidence, denseDetailDemotionEvidence)
                };
                denseLocalDetailPlacementDemoted++;
                evidenceUpdated++;
                assessment = denseDetailDemotedAssessment;
            }
            else if (assessment is not null
                && TryDemoteNonOrthogonalDimensionLikePlacementReadyWallEvidence(
                    updatedWall,
                    assessment,
                    component,
                    wallRoomIds.Length,
                    sharedWallIds.Contains(wall.Id),
                    sideEvidence,
                    hasGeometricRoomBoundarySupport,
                    hasExteriorShellContinuitySupport,
                    out var nonOrthogonalDemotedAssessment,
                    out var nonOrthogonalDemotionEvidence))
            {
                updatedAssessmentsByWallId[wall.Id] = nonOrthogonalDemotedAssessment;
                updatedWall = updatedWall with
                {
                    Evidence = AppendEvidence(updatedWall.Evidence, nonOrthogonalDemotionEvidence)
                };
                nonOrthogonalDimensionLikePlacementDemoted++;
                evidenceUpdated++;
                assessment = nonOrthogonalDemotedAssessment;
            }
            else if (assessment is not null
                && TryDemoteShortDimensionLikePlacementReadyWallEvidence(
                    updatedWall,
                    assessment,
                    context.Options,
                    component,
                    wallRoomIds.Length,
                    sharedWallIds.Contains(wall.Id),
                    sideEvidence,
                    hasGeometricRoomBoundarySupport,
                    hasExteriorShellContinuitySupport,
                    out var shortDimensionDemotedAssessment,
                    out var shortDimensionDemotionEvidence))
            {
                updatedAssessmentsByWallId[wall.Id] = shortDimensionDemotedAssessment;
                updatedWall = updatedWall with
                {
                    Evidence = AppendEvidence(updatedWall.Evidence, shortDimensionDemotionEvidence)
                };
                shortDimensionLikePlacementDemoted++;
                evidenceUpdated++;
                assessment = shortDimensionDemotedAssessment;
            }
            else if (assessment is not null
                && hasExteriorShellContinuitySupport
                && IsRetainedByExteriorShellContinuity(updatedWall, assessment, context.Options))
            {
                var continuityEvidence = new[]
                {
                    "wall evidence: exterior shell continuity kept fragmented paired wall placement-ready between trusted collinear exterior wall segments"
                };
                var retainedAssessment = assessment with
                {
                    Evidence = AppendEvidence(assessment.Evidence, continuityEvidence)
                };
                updatedAssessmentsByWallId[wall.Id] = retainedAssessment;
                updatedWall = updatedWall with
                {
                    Evidence = AppendEvidence(updatedWall.Evidence, continuityEvidence)
                };
                fragmentedExteriorShellContinuityRetained++;
                evidenceUpdated++;
                assessment = retainedAssessment;
            }

            if (assessment is not null
                && TryPromoteRoomConfirmedWallEvidence(
                    updatedWall,
                    assessment,
                    component,
                    wallRoomIds.Length,
                    sharedWallIds.Contains(wall.Id),
                    supportedTopologyEndpointCountsByWallId.TryGetValue(wall.Id, out var supportedEndpointCount)
                        ? supportedEndpointCount
                        : 0,
                    hasOutdoorRoomReference,
                    sideEvidence,
                    context.Options,
                    out var promotedAssessment,
                    out var promotionEvidence))
            {
                updatedAssessmentsByWallId[wall.Id] = promotedAssessment;
                updatedWall = updatedWall with
                {
                    Evidence = AppendEvidence(updatedWall.Evidence, promotionEvidence)
                };
                roomConfirmedPlacementPromoted++;
                evidenceUpdated++;
                assessment = promotedAssessment;
            }

            if (assessment is not null
                && TryPromoteTopologySupportedFragmentedPairEvidence(
                    updatedWall,
                    assessment,
                    component,
                    supportedTopologyEndpointCountsByWallId.TryGetValue(wall.Id, out var promotionSupportedEndpointCount)
                        ? promotionSupportedEndpointCount
                        : 0,
                    hasOutdoorRoomReference,
                    sideEvidence,
                    context.Options,
                    out var topologyPromotedAssessment,
                    out var topologyPromotionEvidence))
            {
                updatedAssessmentsByWallId[wall.Id] = topologyPromotedAssessment;
                updatedWall = updatedWall with
                {
                    Evidence = AppendEvidence(updatedWall.Evidence, topologyPromotionEvidence)
                };
                topologySupportedFragmentedPairPromoted++;
                evidenceUpdated++;
                assessment = topologyPromotedAssessment;
            }

            if (assessment is not null
                && hasGeometricRoomBoundarySupport
                && !assessment.RejectedAsNoise)
            {
                var roomBoundaryEvidence = new[]
                {
                    "wall evidence: geometric room boundary support from reliable room-boundary alignment"
                };
                var assessmentEvidence = AppendEvidence(assessment.Evidence, roomBoundaryEvidence);
                var wallEvidence = AppendEvidence(updatedWall.Evidence, roomBoundaryEvidence);
                var addedEvidence =
                    assessmentEvidence.Count != assessment.Evidence.Count
                    || wallEvidence.Count != updatedWall.Evidence.Count;
                var updatedAssessment = assessment with
                {
                    Evidence = assessmentEvidence
                };
                updatedAssessmentsByWallId[wall.Id] = updatedAssessment;
                updatedWall = updatedWall with
                {
                    Evidence = wallEvidence
                };
                assessment = updatedAssessment;
                if (addedEvidence)
                {
                    geometricRoomBoundaryEvidenceAdded++;
                    evidenceUpdated++;
                }
            }

            if (assessment is not null
                && ShouldAddExplicitRoomBoundarySupportEvidence(
                    updatedWall,
                    assessment,
                    component,
                    wallRoomIds.Length,
                    hasOutdoorRoomReference))
            {
                var explicitRoomBoundaryEvidence = new[]
                {
                    "wall evidence: explicit room boundary support from detected room wall reference"
                };
                var assessmentEvidence = AppendEvidence(assessment.Evidence, explicitRoomBoundaryEvidence);
                var wallEvidence = AppendEvidence(updatedWall.Evidence, explicitRoomBoundaryEvidence);
                var addedEvidence =
                    assessmentEvidence.Count != assessment.Evidence.Count
                    || wallEvidence.Count != updatedWall.Evidence.Count;
                var updatedAssessment = assessment with
                {
                    Evidence = assessmentEvidence
                };
                updatedAssessmentsByWallId[wall.Id] = updatedAssessment;
                updatedWall = updatedWall with
                {
                    Evidence = wallEvidence
                };
                assessment = updatedAssessment;
                if (addedEvidence)
                {
                    explicitRoomBoundaryEvidenceAdded++;
                    evidenceUpdated++;
                }
            }

            if (!ReferenceEquals(updatedWall, wall))
            {
                context.Walls[index] = updatedWall;
            }
        }

        if (updatedAssessmentsByWallId.Count > 0)
        {
            context.WallEvidenceMap = context.WallEvidenceMap with
            {
                WallAssessments = context.WallEvidenceMap.WallAssessments
                    .Select(assessment => updatedAssessmentsByWallId.TryGetValue(assessment.WallId, out var updated)
                        ? updated
                        : assessment)
                    .ToArray()
            };
        }

        AddDiagnostics(
            context,
            changed,
            evidenceUpdated,
            roomReferenced,
            twoSidedRoomEvidence,
            oneSidedRoomEvidence,
            rejectedEvidenceProtected,
            roomConfirmedPlacementPromoted,
            topologySupportedFragmentedPairPromoted,
            fragmentedPairPlacementDemoted,
            denseLocalDetailPlacementDemoted,
            nonOrthogonalDimensionLikePlacementDemoted,
            shortDimensionLikePlacementDemoted,
            fragmentedExteriorShellContinuityRetained,
            geometricRoomBoundaryEvidenceAdded,
            explicitRoomBoundaryEvidenceAdded,
            roomWallReferences.GeometricRoomBoundaryReferencedWallCount,
            roomWallReferences.GeometricRoomBoundaryReferenceCount);
        return ValueTask.CompletedTask;
    }

    private static HashSet<string> BuildSharedWallIds(RoomAdjacencyGraph graph) =>
        graph.Edges
            .SelectMany(edge => edge.SharedWallIds)
            .ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, WallGraphComponent> BuildComponentsByWallId(WallGraph graph)
    {
        var result = new Dictionary<string, WallGraphComponent>(StringComparer.Ordinal);
        foreach (var component in graph.Components)
        {
            foreach (var wallId in component.WallIds)
            {
                result[wallId] = component;
            }
        }

        return result;
    }

    private static Dictionary<string, int> BuildSupportedTopologyEndpointCounts(WallGraph graph)
    {
        if (graph.Edges.Count == 0 || graph.Nodes.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var nodeDegreeById = graph.Nodes.ToDictionary(node => node.Id, node => node.Degree, StringComparer.Ordinal);
        var supportedNodeIdsByWallId = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges.Where(edge => !string.IsNullOrWhiteSpace(edge.WallId)))
        {
            Count(edge.WallId, edge.FromNodeId);
            Count(edge.WallId, edge.ToNodeId);
        }

        return supportedNodeIdsByWallId.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Count,
            StringComparer.Ordinal);

        void Count(string wallId, string nodeId)
        {
            if (!nodeDegreeById.TryGetValue(nodeId, out var degree) || degree <= 1)
            {
                return;
            }

            if (!supportedNodeIdsByWallId.TryGetValue(wallId, out var nodeIds))
            {
                nodeIds = new HashSet<string>(StringComparer.Ordinal);
                supportedNodeIdsByWallId[wallId] = nodeIds;
            }

            nodeIds.Add(nodeId);
        }
    }

    private static HashSet<string> BuildExteriorContinuitySupportedWallIds(
        IReadOnlyList<WallSegment> walls,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> evidenceByWallId,
        ScannerOptions options)
    {
        var trustedExteriorWalls = walls
            .Where(wall => wall.WallType == WallType.Exterior)
            .Where(wall => evidenceByWallId.TryGetValue(wall.Id, out var assessment)
                && assessment.PlacementReady
                && !assessment.RequiresReview
                && assessment.Decision == WallEvidenceDecision.Accept)
            .ToArray();
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var wall in trustedExteriorWalls)
        {
            if (wall.DetectionKind != WallDetectionKind.ParallelLinePair
                || wall.PairEvidence is null
                || !IsSevereFragmentedPairDemotionCandidate(
                    wall,
                    wall.Evidence.Concat(evidenceByWallId[wall.Id].Evidence),
                    options))
            {
                continue;
            }

            if (HasTrustedExteriorShellContinuity(wall, trustedExteriorWalls, options))
            {
                result.Add(wall.Id);
            }
        }

        return result;
    }

    private static bool HasTrustedExteriorShellContinuity(
        WallSegment wall,
        IReadOnlyList<WallSegment> trustedExteriorWalls,
        ScannerOptions options)
    {
        if (!TryResolveAxisInterval(wall.CenterLine, out var orientation, out var coordinate, out var start, out var end))
        {
            return false;
        }

        var coordinateTolerance = Math.Max(options.WallSnapTolerance * 3.0, options.DefaultWallThickness * 1.5);
        var gapTolerance = Math.Max(options.MaxOpeningGap * 0.25, options.DefaultWallThickness * 3.0);
        var hasBeforeSupport = false;
        var hasAfterSupport = false;
        foreach (var other in trustedExteriorWalls)
        {
            if (string.Equals(other.Id, wall.Id, StringComparison.Ordinal)
                || other.PageNumber != wall.PageNumber
                || !TryResolveAxisInterval(other.CenterLine, out var otherOrientation, out var otherCoordinate, out var otherStart, out var otherEnd)
                || otherOrientation != orientation
                || Math.Abs(otherCoordinate - coordinate) > coordinateTolerance)
            {
                continue;
            }

            var beforeGap = start - otherEnd;
            if (beforeGap >= -coordinateTolerance && beforeGap <= gapTolerance)
            {
                hasBeforeSupport = true;
            }

            var afterGap = otherStart - end;
            if (afterGap >= -coordinateTolerance && afterGap <= gapTolerance)
            {
                hasAfterSupport = true;
            }

            if (hasBeforeSupport && hasAfterSupport)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveAxisInterval(
        PlanLineSegment line,
        out AxisOrientation orientation,
        out double coordinate,
        out double start,
        out double end)
    {
        var dx = Math.Abs(line.End.X - line.Start.X);
        var dy = Math.Abs(line.End.Y - line.Start.Y);
        if (dx >= dy && dy <= Math.Max(1.0, dx * 0.02))
        {
            orientation = AxisOrientation.Horizontal;
            coordinate = (line.Start.Y + line.End.Y) / 2.0;
            start = Math.Min(line.Start.X, line.End.X);
            end = Math.Max(line.Start.X, line.End.X);
            return true;
        }

        if (dy > dx && dx <= Math.Max(1.0, dy * 0.02))
        {
            orientation = AxisOrientation.Vertical;
            coordinate = (line.Start.X + line.End.X) / 2.0;
            start = Math.Min(line.Start.Y, line.End.Y);
            end = Math.Max(line.Start.Y, line.End.Y);
            return true;
        }

        orientation = AxisOrientation.Unknown;
        coordinate = 0;
        start = 0;
        end = 0;
        return false;
    }

    private static Dictionary<string, WallEvidenceWallAssessment> BuildRejectedEvidenceByWallId(WallEvidenceMap evidenceMap) =>
        evidenceMap.WallAssessments
            .Where(assessment => assessment.RejectedAsNoise || assessment.Decision == WallEvidenceDecision.Reject)
            .Where(assessment => !string.IsNullOrWhiteSpace(assessment.WallId))
            .GroupBy(assessment => assessment.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    private static Dictionary<string, WallEvidenceWallAssessment> BuildEvidenceByWallId(WallEvidenceMap evidenceMap) =>
        evidenceMap.WallAssessments
            .Where(assessment => !string.IsNullOrWhiteSpace(assessment.WallId))
            .GroupBy(assessment => assessment.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    private static WallTypeRefinement RefineWallType(
        WallSegment wall,
        WallEvidenceWallAssessment? assessment,
        int roomReferenceCount,
        bool isSharedByRoomAdjacency,
        bool hasOutdoorRoomReference,
        RoomSideEvidence sideEvidence,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? rejectedEvidence,
        PlanRect? mainFloorplanBounds,
        ScannerOptions options)
    {
        if (rejectedEvidence is not null && IsNonStructuralWallComponent(component))
        {
            return new WallTypeRefinement(
                WallType.Unknown,
                $"wall type refined unknown: wall belongs to non-structural or isolated graph component; Wall Evidence V2 rejected candidate as {rejectedEvidence.Category}");
        }

        if (rejectedEvidence is not null)
        {
            return new WallTypeRefinement(
                WallType.Unknown,
                $"wall type refined unknown: Wall Evidence V2 rejected candidate as {rejectedEvidence.Category}");
        }

        if (IsNonStructuralWallComponent(component))
        {
            return new WallTypeRefinement(
                WallType.Unknown,
                "wall type refined unknown: wall belongs to non-structural or isolated graph component");
        }

        if (wall.FragmentEvidence?.RequiresGeometryReview == true)
        {
            return new WallTypeRefinement(
                WallType.Unknown,
                "wall type refined unknown: fragment-merged wall geometry requires review before exact placement");
        }

        if (IsTrustedRecoveredExteriorShellCandidate(
            wall,
            assessment,
            component,
            mainFloorplanBounds,
            options))
        {
            return new WallTypeRefinement(
                WallType.Exterior,
                "wall type refined exterior: recovered wall body aligned to main floorplan perimeter shell");
        }

        if (isSharedByRoomAdjacency)
        {
            if (hasOutdoorRoomReference)
            {
                if (IsUntrustedOutdoorExteriorPromotionCandidate(wall, assessment))
                {
                    var refinedType = IsRecoveredMissingWallCandidate(wall)
                        ? WallType.Interior
                        : WallType.Unknown;
                    return new WallTypeRefinement(
                        refinedType,
                        refinedType == WallType.Interior
                            ? "wall type refined interior: shared outdoor/terrace room evidence is not trusted as exterior without shell support; outdoor covered-area boundary"
                            : "wall type refined unknown: shared outdoor/terrace room evidence is not trusted as exterior without shell support; outdoor covered-area boundary");
                }

                return new WallTypeRefinement(
                    WallType.Exterior,
                    "wall type refined exterior: shared by room adjacency that includes outdoor/terrace room evidence");
            }

            if (wall.WallType == WallType.Exterior)
            {
                return new WallTypeRefinement(
                    WallType.Interior,
                    "wall type refined interior: shared by room adjacency boundary overrides exterior envelope/local-boundary guess");
            }

            return new WallTypeRefinement(
                WallType.Interior,
                "wall type refined interior: shared by room adjacency boundary");
        }

        if (sideEvidence.HasRoomsOnBothSides)
        {
            if (sideEvidence.HasOutdoorRoomSide)
            {
                if (IsUntrustedOutdoorExteriorPromotionCandidate(wall, assessment))
                {
                    var refinedType = IsRecoveredMissingWallCandidate(wall)
                        ? WallType.Interior
                        : WallType.Unknown;
                    return new WallTypeRefinement(
                        refinedType,
                        refinedType == WallType.Interior
                            ? "wall type refined interior: two-sided outdoor/terrace room evidence is not trusted as exterior without shell support; outdoor covered-area boundary"
                            : "wall type refined unknown: two-sided outdoor/terrace room evidence is not trusted as exterior without shell support; outdoor covered-area boundary");
                }

                return new WallTypeRefinement(
                    WallType.Exterior,
                    "wall type refined exterior: room evidence on both sides includes outdoor/terrace space");
            }

            if (wall.WallType == WallType.Exterior)
            {
                return new WallTypeRefinement(
                    WallType.Interior,
                    "wall type refined interior: detected room evidence on both sides overrides exterior envelope/local-boundary guess");
            }

            return new WallTypeRefinement(
                WallType.Interior,
                "wall type refined interior: detected room evidence on both sides");
        }

        if (sideEvidence.HasRoomsOnExactlyOneSide
            && IsStructuralWallComponent(component)
            && wall.Confidence.Value >= 0.45)
        {
            if (sideEvidence.HasOutdoorRoomSide)
            {
                if (IsUntrustedOutdoorExteriorPromotionCandidate(wall, assessment))
                {
                    var refinedType = IsRecoveredMissingWallCandidate(wall)
                        ? WallType.Interior
                        : WallType.Unknown;
                    return new WallTypeRefinement(
                        refinedType,
                        refinedType == WallType.Interior
                            ? "wall type refined interior: recovered uncertain wall candidate with one-sided outdoor/terrace room evidence is not trusted as exterior without shell support"
                            : "wall type refined unknown: one-sided outdoor/terrace room evidence alone is not trusted as exterior shell support for uncertain local-boundary wall candidate");
                }

                return new WallTypeRefinement(
                    WallType.Exterior,
                    "wall type refined exterior: detected room evidence on one side is outdoor/terrace space");
            }

            if (wall.WallType == WallType.Interior)
            {
                return new WallTypeRefinement(
                    WallType.Interior,
                    "wall type preserved interior: one-sided room evidence did not override interior wall-envelope evidence");
            }

            if (wall.WallType == WallType.Unknown && IsRecoveredMissingWallCandidate(wall))
            {
                return new WallTypeRefinement(
                    WallType.Interior,
                    "wall type refined interior: recovered missing-wall candidate with one-sided room evidence is not trusted as exterior without shell or outdoor evidence");
            }

            return new WallTypeRefinement(
                WallType.Exterior,
                "wall type refined exterior: detected room evidence on one side only");
        }

        if (wall.WallType == WallType.Unknown
            && roomReferenceCount == 1
            && IsStructuralWallComponent(component)
            && wall.Confidence.Value >= 0.6)
        {
            if (IsRecoveredMissingWallCandidate(wall))
            {
                return new WallTypeRefinement(
                    WallType.Interior,
                    "wall type refined interior: recovered structural room boundary is not trusted as exterior without shell, outdoor, or side evidence");
            }

            return new WallTypeRefinement(
                WallType.Exterior,
                "wall type refined exterior: structural room boundary with no shared room side");
        }

        if (IsTrustedRecoveredInteriorWallBodyCandidate(
            wall,
            assessment,
            component,
            mainFloorplanBounds,
            options))
        {
            return new WallTypeRefinement(
                WallType.Interior,
                $"wall type refined interior: {WallPlacementContextGuards.TrustedRecoveredMainStructuralInteriorEvidence} inside floorplan envelope");
        }

        return new WallTypeRefinement(wall.WallType, "wall type unchanged: room-side evidence was inconclusive");
    }

    private static bool IsStructuralWallComponent(WallGraphComponent? component) =>
        component is null
        || (!component.ExcludedFromStructuralTopology
            && component.Kind is WallGraphComponentKind.MainStructural or WallGraphComponentKind.SecondaryStructural);

    private static bool IsNonStructuralWallComponent(WallGraphComponent? component) =>
        component is not null
        && (component.ExcludedFromStructuralTopology
            || component.Kind is WallGraphComponentKind.ObjectLikeIsland);

    private static bool IsRecoveredMissingWallCandidate(WallSegment wall) =>
        wall.Evidence.Any(item =>
            item.Contains("recovered by wall evidence map", StringComparison.OrdinalIgnoreCase)
            || item.Contains("missing-wall recovery", StringComparison.OrdinalIgnoreCase));

    private static bool IsTrustedRecoveredExteriorShellCandidate(
        WallSegment wall,
        WallEvidenceWallAssessment? assessment,
        WallGraphComponent? component,
        PlanRect? mainFloorplanBounds,
        ScannerOptions options)
    {
        if (mainFloorplanBounds is null
            || wall.WallType != WallType.Unknown
            || !IsRecoveredMissingWallCandidate(wall)
            || !IsStructuralWallComponent(component)
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.DrawingLength < Math.Max(72.0, options.MinWallLength * 3.0)
            || wall.Confidence.Value < 0.80
            || wall.PairEvidence is not { } pair
            || pair.Score < 0.80
            || pair.OverlapRatio < 0.95
            || pair.FaceSeparation < 2.0
            || pair.FaceSeparation > Math.Max(18.0, options.DefaultWallThickness * 5.0)
            || assessment is null
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category != WallEvidenceCategory.RecoveredWallBody)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();
        if (evidence.Any(IsRecoveredExteriorShellBlockingEvidence))
        {
            return false;
        }

        return IsWallLineNearMainFloorplanPerimeter(wall.CenterLine, mainFloorplanBounds.Value, options);
    }

    private static bool IsTrustedRecoveredInteriorWallBodyCandidate(
        WallSegment wall,
        WallEvidenceWallAssessment? assessment,
        WallGraphComponent? component,
        PlanRect? mainFloorplanBounds,
        ScannerOptions options) =>
        mainFloorplanBounds is not null
        && wall.WallType == WallType.Unknown
        && WallPlacementContextGuards.IsTrustedRecoveredMainStructuralInteriorWallBody(
            wall,
            component,
            assessment,
            component?.Evidence)
        && !IsWallLineNearMainFloorplanPerimeter(wall.CenterLine, mainFloorplanBounds.Value, options);

    private static bool IsWallLineNearMainFloorplanPerimeter(
        PlanLineSegment line,
        PlanRect bounds,
        ScannerOptions options)
    {
        var tolerance = Math.Max(24.0, Math.Max(options.DefaultWallThickness * 6.0, options.WallSnapTolerance * 8.0));
        var overlapTolerance = Math.Max(8.0, options.DefaultWallThickness * 2.0);
        if (line.IsVertical(options.GeometryTolerance.Distance))
        {
            var x = line.Midpoint.X;
            var start = Math.Min(line.Start.Y, line.End.Y);
            var end = Math.Max(line.Start.Y, line.End.Y);
            var axisOverlap = Math.Min(end, bounds.Bottom + overlapTolerance)
                - Math.Max(start, bounds.Top - overlapTolerance);
            return axisOverlap > Math.Max(12.0, line.Length * 0.35)
                && (Math.Abs(x - bounds.Left) <= tolerance || Math.Abs(x - bounds.Right) <= tolerance);
        }

        if (line.IsHorizontal(options.GeometryTolerance.Distance))
        {
            var y = line.Midpoint.Y;
            var start = Math.Min(line.Start.X, line.End.X);
            var end = Math.Max(line.Start.X, line.End.X);
            var axisOverlap = Math.Min(end, bounds.Right + overlapTolerance)
                - Math.Max(start, bounds.Left - overlapTolerance);
            return axisOverlap > Math.Max(12.0, line.Length * 0.35)
                && (Math.Abs(y - bounds.Top) <= tolerance || Math.Abs(y - bounds.Bottom) <= tolerance);
        }

        return false;
    }

    private static bool IsRecoveredExteriorShellBlockingEvidence(string evidence) =>
        evidence.Contains("outdoor covered-area boundary", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("unpaired outdoor covered-area boundary", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("covered-area boundary", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("outdoor/terrace room evidence alone", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("terrace", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("covered entry", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("covered-entry", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("overbygd", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("canopy", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("railing", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("trim/detail", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("trim linework", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("glazing", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("detail linework", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("not trusted", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("without shell support", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("alone is not trusted", StringComparison.OrdinalIgnoreCase);

    private static bool IsUntrustedOutdoorExteriorPromotionCandidate(
        WallSegment wall,
        WallEvidenceWallAssessment? assessment)
    {
        var evidence = wall.Evidence
            .Concat(assessment?.Evidence ?? Array.Empty<string>())
            .Concat(assessment?.ScoreBreakdown.PositiveEvidence ?? Array.Empty<string>())
            .Concat(assessment?.ScoreBreakdown.NegativeEvidence ?? Array.Empty<string>())
            .ToArray();
        if (HasTrustedExteriorShellSupport(evidence))
        {
            return false;
        }

        var localBoundaryOnly = evidence.Any(item =>
            item.Contains("local outer boundary", StringComparison.OrdinalIgnoreCase)
            || item.Contains("floorplan/wall envelope", StringComparison.OrdinalIgnoreCase));
        var weakOrUnknownLayer = evidence.Any(item =>
            item.Contains("layer evidence: no strong layer", StringComparison.OrdinalIgnoreCase)
            || item.Contains("classified Unknown", StringComparison.OrdinalIgnoreCase)
            || item.Contains("source layer category Unknown", StringComparison.OrdinalIgnoreCase));
        var uncertainEvidenceCategory = assessment?.Category is WallEvidenceCategory.MediumWallBody
            or WallEvidenceCategory.RecoveredWallBody
            or WallEvidenceCategory.WeakSingleLine;
        var uncertainGeometry = IsRecoveredMissingWallCandidate(wall)
            || wall.DetectionKind is WallDetectionKind.SingleLine or WallDetectionKind.FragmentMerged;

        return (localBoundaryOnly && weakOrUnknownLayer && uncertainEvidenceCategory)
            || uncertainGeometry;
    }

    private static bool HasTrustedExteriorShellSupport(IEnumerable<string> evidence) =>
        evidence.Any(item =>
            !item.Contains("not trusted", StringComparison.OrdinalIgnoreCase)
            && !item.Contains("without shell support", StringComparison.OrdinalIgnoreCase)
            && !item.Contains("alone is not", StringComparison.OrdinalIgnoreCase)
            && (item.Contains("exterior shell", StringComparison.OrdinalIgnoreCase)
                || item.Contains("wall-like layer", StringComparison.OrdinalIgnoreCase)
                || item.Contains("trusted benchmark", StringComparison.OrdinalIgnoreCase)));

    private static bool TryPromoteRoomConfirmedWallEvidence(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        WallGraphComponent? component,
        int roomReferenceCount,
        bool isSharedByRoomAdjacency,
        int supportedTopologyEndpointCount,
        bool hasOutdoorRoomReference,
        RoomSideEvidence sideEvidence,
        ScannerOptions options,
        out WallEvidenceWallAssessment promotedAssessment,
        out IReadOnlyList<string> promotionEvidence)
    {
        promotedAssessment = assessment;
        promotionEvidence = Array.Empty<string>();

        if (assessment.PlacementReady
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || !assessment.RequiresReview)
        {
            return false;
        }

        if (assessment.Category is not (WallEvidenceCategory.MediumWallBody or WallEvidenceCategory.RecoveredWallBody))
        {
            return false;
        }

        var isRoomConfirmableIsolatedComponent = IsRoomConfirmableIsolatedWallComponent(
            wall,
            assessment,
            component,
            options);
        if ((!IsStructuralWallComponent(component)
            && !isRoomConfirmableIsolatedComponent)
            || wall.WallType == WallType.Unknown
            || wall.FragmentEvidence?.RequiresGeometryReview == true)
        {
            return false;
        }

        var hasRoomBoundaryFragmentConfirmation = IsTrustedFragmentMergedInteriorRoomBoundary(
            wall,
            assessment,
            roomReferenceCount,
            supportedTopologyEndpointCount,
            sideEvidence,
            options);
        if (hasOutdoorRoomReference
            || (sideEvidence.HasOutdoorRoomSide && !hasRoomBoundaryFragmentConfirmation))
        {
            return false;
        }

        if (HasRoomConfirmedPromotionBlocker(wall, assessment))
        {
            return false;
        }

        var hasStrongRoomConfirmation =
            isSharedByRoomAdjacency
            || roomReferenceCount >= 2
            || sideEvidence.HasRoomsOnBothSides;
        var hasShortStructuralReturnConfirmation =
            roomReferenceCount >= 1
            && supportedTopologyEndpointCount >= 2
            && IsTrustedShortStructuralReturnWall(wall, assessment, options);
        if (!hasStrongRoomConfirmation
            && !hasShortStructuralReturnConfirmation
            && !hasRoomBoundaryFragmentConfirmation)
        {
            return false;
        }

        if (!HasWallBodyEvidence(wall, assessment)
            && !hasRoomBoundaryFragmentConfirmation)
        {
            return false;
        }

        promotionEvidence = new[]
        {
            "wall evidence: room-confirmed wall body promoted to placement-ready after room adjacency refinement",
            $"wall evidence: room references {roomReferenceCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, shared adjacency {isSharedByRoomAdjacency.ToString(System.Globalization.CultureInfo.InvariantCulture)}, two-sided room evidence {sideEvidence.HasRoomsOnBothSides.ToString(System.Globalization.CultureInfo.InvariantCulture)}, topology-supported endpoints {supportedTopologyEndpointCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        }
        .Concat(isRoomConfirmableIsolatedComponent
            ? new[] { $"wall evidence: {WallPlacementReadinessEvaluator.RoomConfirmedIsolatedFragmentPromotionEvidence} because room boundary evidence overrode early isolated graph classification" }
            : Array.Empty<string>())
        .Concat(hasShortStructuralReturnConfirmation
            ? new[] { "wall evidence: short structural return promoted by room boundary and two supported topology endpoints" }
            : Array.Empty<string>())
        .Concat(hasRoomBoundaryFragmentConfirmation
            ? new[] { "wall evidence: clean fragment-merged interior room boundary promoted after room refinement confirmed it belongs to a detected room boundary" }
            : Array.Empty<string>())
        .ToArray();
        promotedAssessment = assessment with
        {
            PlacementReady = true,
            RequiresReview = false,
            Decision = WallEvidenceDecision.Accept,
            Evidence = AppendEvidence(assessment.Evidence, promotionEvidence)
        };
        return true;
    }

    private static bool IsRoomConfirmableIsolatedWallComponent(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        WallGraphComponent? component,
        ScannerOptions options)
    {
        if (component is null
            || component.ExcludedFromStructuralTopology
            || component.Kind != WallGraphComponentKind.IsolatedFragment
            || wall.WallType != WallType.Interior
            || wall.DetectionKind is WallDetectionKind.SingleLine or WallDetectionKind.Unknown
            || wall.FragmentEvidence?.RequiresGeometryReview == true
            || wall.DrawingLength < RoomConfirmableIsolatedWallMinimumLength(wall, options)
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category is not (WallEvidenceCategory.MediumWallBody
                or WallEvidenceCategory.RecoveredWallBody))
        {
            return false;
        }

        return HasWallBodyEvidence(wall, assessment)
            && !HasRoomConfirmedPromotionBlocker(wall, assessment);
    }

    private static double RoomConfirmableIsolatedWallMinimumLength(
        WallSegment wall,
        ScannerOptions options)
    {
        var minimum = Math.Max(options.MinWallLength * 3.0, options.DefaultWallThickness * 18.0);
        return wall.DetectionKind == WallDetectionKind.FragmentMerged
            ? Math.Max(minimum, options.DefaultWallThickness * 20.0)
            : minimum;
    }

    private static bool IsTrustedFragmentMergedInteriorRoomBoundary(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        int roomReferenceCount,
        int supportedTopologyEndpointCount,
        RoomSideEvidence sideEvidence,
        ScannerOptions options)
    {
        if (roomReferenceCount < 1
            || wall.DetectionKind != WallDetectionKind.FragmentMerged
            || wall.WallType != WallType.Interior
            || wall.PairEvidence is not null
            || wall.FragmentEvidence is not { RequiresGeometryReview: false } fragmentEvidence
            || assessment.Category != WallEvidenceCategory.MediumWallBody)
        {
            return false;
        }

        var minimumLength = Math.Max(
            72.0,
            Math.Max(options.MinWallLength * 3.5, wall.Thickness * 10.0));
        if (wall.DrawingLength < minimumLength)
        {
            return false;
        }

        var uniqueSourcePrimitiveCount = Math.Max(0, wall.SourcePrimitiveIds.Count - fragmentEvidence.DuplicatePrimitiveCount);
        var fragmentCount = Math.Max(fragmentEvidence.FragmentCount, uniqueSourcePrimitiveCount);
        var cleanDuplicatedRoomBoundary =
            roomReferenceCount > 0
            && fragmentCount <= 4
            && fragmentEvidence.DuplicatePrimitiveCount <= 8
            && fragmentEvidence.GapRatio <= 0.001
            && fragmentEvidence.TotalHealedGap <= 0.001;
        if (fragmentCount is < 2 or > 8
            || (fragmentEvidence.DuplicatePrimitiveCount > 3 && !cleanDuplicatedRoomBoundary)
            || fragmentEvidence.GapRatio > 0.01
            || fragmentEvidence.TotalHealedGap > Math.Max(2.0, wall.Thickness * 0.35))
        {
            return false;
        }

        var evidence = assessment.Evidence
            .Concat(wall.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();
        if (!evidence.Any(item => item.Contains("only one trusted structural endpoint", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return supportedTopologyEndpointCount > 0
            || sideEvidence.PositiveRoomHits + sideEvidence.NegativeRoomHits > 0
            || evidence.Any(item =>
                item.Contains("supported wall evidence inside exterior envelope", StringComparison.OrdinalIgnoreCase)
                || item.Contains("structural context", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryPromoteTopologySupportedFragmentedPairEvidence(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        WallGraphComponent? component,
        int supportedTopologyEndpointCount,
        bool hasOutdoorRoomReference,
        RoomSideEvidence sideEvidence,
        ScannerOptions options,
        out WallEvidenceWallAssessment promotedAssessment,
        out IReadOnlyList<string> promotionEvidence)
    {
        promotedAssessment = assessment;
        promotionEvidence = Array.Empty<string>();

        if (assessment.PlacementReady
            || !assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category != WallEvidenceCategory.MediumWallBody
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.WallType != WallType.Interior
            || component is null
            || component.Kind != WallGraphComponentKind.MainStructural
            || component.ExcludedFromStructuralTopology
            || supportedTopologyEndpointCount < 2
            || hasOutdoorRoomReference
            || sideEvidence.HasOutdoorRoomSide)
        {
            return false;
        }

        if (HasRoomConfirmedPromotionBlocker(wall, assessment))
        {
            return false;
        }

        var evidence = assessment.Evidence
            .Concat(wall.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();
        if (!evidence.Any(item => item.Contains("noisy fragmented face evidence", StringComparison.OrdinalIgnoreCase))
            || evidence.Any(item => item.Contains("only one structurally supported endpoint", StringComparison.OrdinalIgnoreCase))
            || !HasWallBodyEvidence(wall, assessment)
            || !TryReadPairScore(evidence, out var pairScore)
            || pairScore < 0.70
            || !TryReadFaceFragmentCounts(evidence, out var faceFragments)
            || faceFragments.TotalFaceFragmentCount > 96)
        {
            return false;
        }

        if (TryReadMaxHealedFaceGap(evidence, out var maxHealedGap))
        {
            var localThickness = wall.Thickness > 0
                ? wall.Thickness
                : options.DefaultWallThickness;
            var maxTrustedGap = Math.Max(
                Math.Max(options.WallSnapTolerance * 1.5, 2.5),
                Math.Min(localThickness * 0.9, wall.DrawingLength * 0.12));
            if (maxHealedGap > maxTrustedGap)
            {
                return false;
            }
        }

        promotionEvidence = new[]
        {
            $"wall evidence: {WallPlacementReadinessEvaluator.TopologySupportedFragmentedPairPromotionEvidence} after both endpoints aligned to trusted structural graph",
            $"wall evidence: pair score {pairScore.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, max face fragments {faceFragments.MaxFaceFragmentCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, total face fragments {faceFragments.TotalFaceFragmentCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, topology-supported endpoints {supportedTopologyEndpointCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        };
        promotedAssessment = assessment with
        {
            PlacementReady = true,
            RequiresReview = false,
            Decision = WallEvidenceDecision.Accept,
            Evidence = AppendEvidence(assessment.Evidence, promotionEvidence)
        };
        return true;
    }

    private static bool TryDemoteFragmentedPlacementReadyWallEvidence(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        ScannerOptions options,
        WallGraphComponent? component,
        int roomReferenceCount,
        bool isSharedByRoomAdjacency,
        RoomSideEvidence sideEvidence,
        int supportedTopologyEndpointCount,
        bool hasExteriorShellContinuitySupport,
        out WallEvidenceWallAssessment demotedAssessment,
        out IReadOnlyList<string> demotionEvidence)
    {
        demotedAssessment = assessment;
        demotionEvidence = Array.Empty<string>();

        if (!assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair)
        {
            return false;
        }

        var evidence = assessment.Evidence
            .Concat(wall.Evidence)
            .ToArray();
        if (!HasUnknownOrWeakLayerEvidence(evidence)
            || !TryReadPairScore(evidence, out var pairScore)
            || !TryReadFaceFragmentCounts(evidence, out var faceFragments))
        {
            return false;
        }

        var maxTrustedLength = Math.Max(90, options.MinWallLength * 4.0);
        var shortLowScoreFragmentedPair =
            pairScore < 0.68
            && wall.DrawingLength <= maxTrustedLength
            && faceFragments.MaxFaceFragmentCount >= 40
            && faceFragments.TotalFaceFragmentCount >= 50;
        var hasPlacementContextSupport = HasPlacementContextSupport(
            component,
            roomReferenceCount,
            isSharedByRoomAdjacency,
            sideEvidence,
            supportedTopologyEndpointCount)
            || hasExteriorShellContinuitySupport;
        var unsupportedSeverelyFragmentedPair =
            pairScore < 0.95
            && wall.DrawingLength <= Math.Max(180, options.MinWallLength * 7.5)
            && faceFragments.MaxFaceFragmentCount >= 70
            && faceFragments.TotalFaceFragmentCount >= 80
            && !hasPlacementContextSupport;
        shortLowScoreFragmentedPair = shortLowScoreFragmentedPair
            && !hasExteriorShellContinuitySupport;
        if (!shortLowScoreFragmentedPair && !unsupportedSeverelyFragmentedPair)
        {
            return false;
        }

        var reason = unsupportedSeverelyFragmentedPair
            ? "unsupported severe fragmented-face evidence"
            : "severe fragmented-face evidence";
        demotionEvidence = new[]
        {
            $"wall evidence: demoted from placement-ready because unlayered parallel-face pair has {reason}; pair score {pairScore.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, max face fragments {faceFragments.MaxFaceFragmentCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, total face fragments {faceFragments.TotalFaceFragmentCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, room refs {roomReferenceCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, side room hits {(sideEvidence.PositiveRoomHits + sideEvidence.NegativeRoomHits).ToString(System.Globalization.CultureInfo.InvariantCulture)}, supported endpoints {supportedTopologyEndpointCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        };
        demotedAssessment = assessment with
        {
            Category = assessment.Category == WallEvidenceCategory.StrongWallBody
                ? WallEvidenceCategory.MediumWallBody
                : assessment.Category,
            PlacementReady = false,
            RequiresReview = true,
            Decision = WallEvidenceDecision.Review,
            Evidence = AppendEvidence(assessment.Evidence, demotionEvidence)
        };
        return true;
    }

    private static bool TryDemoteDenseLocalDetailPlacementReadyWallEvidence(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        ScannerOptions options,
        IReadOnlyList<WallSegment> walls,
        WallGraphComponent? component,
        int roomReferenceCount,
        bool isSharedByRoomAdjacency,
        RoomSideEvidence sideEvidence,
        int supportedTopologyEndpointCount,
        bool hasGeometricRoomBoundarySupport,
        bool hasExteriorShellContinuitySupport,
        out WallEvidenceWallAssessment demotedAssessment,
        out IReadOnlyList<string> demotionEvidence)
    {
        demotedAssessment = assessment;
        demotionEvidence = Array.Empty<string>();

        if (!assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || wall.WallType == WallType.Exterior
            || wall.DrawingLength > DenseLocalDetailDemotionMaxWallLength(options)
            || hasExteriorShellContinuitySupport)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .ToArray();
        var hasDimensionLikeWeakLayer = IsDimensionLikeWeakLayerEvidence(evidence);
        var hasPlacementContext = roomReferenceCount > 0
            || isSharedByRoomAdjacency
            || sideEvidence.HasRoomsOnBothSides;
        if (!HasUnknownOrWeakLayerEvidence(evidence)
            || !IsDenseLocalDetailNeighborhood(wall, walls, options, out var nearbyCount, out var shortNearbyCount, out var offAxisNearbyCount)
            || (hasPlacementContext && !hasDimensionLikeWeakLayer)
            || (hasGeometricRoomBoundarySupport && !hasDimensionLikeWeakLayer)
            || IsTrustedDimensionLikeDenseRoomBoundaryWall(
                wall,
                component,
                evidence,
                roomReferenceCount,
                supportedTopologyEndpointCount,
                hasGeometricRoomBoundarySupport,
                offAxisNearbyCount))
        {
            return false;
        }

        demotionEvidence =
        [
            $"wall evidence: demoted from placement-ready because short unlayered wall candidate sits inside dense local detail/stair-like linework; nearby walls {nearbyCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, short nearby walls {shortNearbyCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, off-axis nearby walls {offAxisNearbyCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, room refs {roomReferenceCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, side room hits {(sideEvidence.PositiveRoomHits + sideEvidence.NegativeRoomHits).ToString(System.Globalization.CultureInfo.InvariantCulture)}, supported endpoints {supportedTopologyEndpointCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, dimension-like weak layer {hasDimensionLikeWeakLayer.ToString(System.Globalization.CultureInfo.InvariantCulture)}, component {component?.Kind.ToString() ?? "Unknown"}"
        ];
        demotedAssessment = assessment with
        {
            Category = assessment.Category == WallEvidenceCategory.StrongWallBody
                ? WallEvidenceCategory.MediumWallBody
                : assessment.Category,
            PlacementReady = false,
            RequiresReview = true,
            Decision = WallEvidenceDecision.Review,
            Evidence = AppendEvidence(assessment.Evidence, demotionEvidence)
        };
        return true;
    }

    private static bool IsTrustedDimensionLikeDenseRoomBoundaryWall(
        WallSegment wall,
        WallGraphComponent? component,
        IReadOnlyList<string> evidence,
        int roomReferenceCount,
        int supportedTopologyEndpointCount,
        bool hasGeometricRoomBoundarySupport,
        int offAxisNearbyCount)
    {
        if (!hasGeometricRoomBoundarySupport
            || roomReferenceCount < 1
            || supportedTopologyEndpointCount < 2
            || offAxisNearbyCount > 1
            || wall.WallType != WallType.Interior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || component is null
            || component.ExcludedFromStructuralTopology
            || component.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment
            || (component.Kind != WallGraphComponentKind.MainStructural
                && wall.DrawingLength < MinSecondaryTrustedDimensionLikeDenseRoomBoundaryLength)
            || !TryReadPairScore(evidence, out var pairScore)
            || pairScore < MinTrustedDimensionLikeDenseRoomBoundaryPairScore
            || !TryReadFaceFragmentCounts(evidence, out var faceFragments)
            || faceFragments.MaxFaceFragmentCount > MaxTrustedDimensionLikeDenseRoomBoundaryFaceFragments
            || faceFragments.TotalFaceFragmentCount > MaxTrustedDimensionLikeDenseRoomBoundaryTotalFaceFragments)
        {
            return false;
        }

        return !evidence.Any(item =>
            item.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
            || item.Contains("object/fixture", StringComparison.OrdinalIgnoreCase)
            || item.Contains("fixture detail", StringComparison.OrdinalIgnoreCase)
            || item.Contains("repeated short detail", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door/opening", StringComparison.OrdinalIgnoreCase)
            || item.Contains("stair", StringComparison.OrdinalIgnoreCase)
            || item.Contains("railing", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered entry", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered-entry", StringComparison.OrdinalIgnoreCase)
            || item.Contains("overbygd", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryDemoteNonOrthogonalDimensionLikePlacementReadyWallEvidence(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        WallGraphComponent? component,
        int roomReferenceCount,
        bool isSharedByRoomAdjacency,
        RoomSideEvidence sideEvidence,
        bool hasGeometricRoomBoundarySupport,
        bool hasExteriorShellContinuitySupport,
        out WallEvidenceWallAssessment demotedAssessment,
        out IReadOnlyList<string> demotionEvidence)
    {
        demotedAssessment = assessment;
        demotionEvidence = Array.Empty<string>();

        if (!assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category != WallEvidenceCategory.MediumWallBody
            || wall.WallType == WallType.Exterior
            || wall.DetectionKind != WallDetectionKind.SingleLine
            || wall.PairEvidence is not null
            || hasGeometricRoomBoundarySupport
            || hasExteriorShellContinuitySupport
            || roomReferenceCount > 0
            || isSharedByRoomAdjacency
            || sideEvidence.HasRoomsOnBothSides)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component?.Evidence ?? Array.Empty<string>())
            .ToArray();
        if (!IsStronglyOffAxisWallLine(wall.CenterLine)
            || !IsDimensionLikeWeakLayerEvidence(evidence)
            || HasExplicitWallBodyEvidence(evidence))
        {
            return false;
        }

        var offAxisRatio = OffAxisRatio(wall.CenterLine);
        demotionEvidence =
        [
            $"wall evidence: demoted from placement-ready because non-orthogonal single-line candidate has dimension-like weak layer evidence and no explicit room-boundary support; off-axis ratio {offAxisRatio.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, room refs {roomReferenceCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, side room hits {(sideEvidence.PositiveRoomHits + sideEvidence.NegativeRoomHits).ToString(System.Globalization.CultureInfo.InvariantCulture)}, component {component?.Kind.ToString() ?? "Unknown"}"
        ];
        demotedAssessment = assessment with
        {
            PlacementReady = false,
            RequiresReview = true,
            Decision = WallEvidenceDecision.Review,
            Evidence = AppendEvidence(assessment.Evidence, demotionEvidence)
        };
        return true;
    }

    private static bool TryDemoteShortDimensionLikePlacementReadyWallEvidence(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        ScannerOptions options,
        WallGraphComponent? component,
        int roomReferenceCount,
        bool isSharedByRoomAdjacency,
        RoomSideEvidence sideEvidence,
        bool hasGeometricRoomBoundarySupport,
        bool hasExteriorShellContinuitySupport,
        out WallEvidenceWallAssessment demotedAssessment,
        out IReadOnlyList<string> demotionEvidence)
    {
        demotedAssessment = assessment;
        demotionEvidence = Array.Empty<string>();

        if (!assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category != WallEvidenceCategory.MediumWallBody
            || wall.WallType == WallType.Exterior
            || wall.DetectionKind is not (WallDetectionKind.SingleLine or WallDetectionKind.FragmentMerged)
            || wall.PairEvidence is not null
            || hasGeometricRoomBoundarySupport
            || hasExteriorShellContinuitySupport
            || roomReferenceCount > 0
            || isSharedByRoomAdjacency
            || sideEvidence.HasRoomsOnBothSides)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component?.Evidence ?? Array.Empty<string>())
            .ToArray();
        if (!IsDimensionLikeWeakLayerEvidence(evidence)
            || HasExplicitWallBodyEvidence(evidence)
            || !IsShortOrFragmentedDimensionLikeLinework(wall, evidence, options))
        {
            return false;
        }

        demotionEvidence =
        [
            $"wall evidence: demoted from placement-ready because short or fragmented dimension-like single-line candidate has no explicit room-boundary support; length {wall.DrawingLength.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, room refs {roomReferenceCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, side room hits {(sideEvidence.PositiveRoomHits + sideEvidence.NegativeRoomHits).ToString(System.Globalization.CultureInfo.InvariantCulture)}, component {component?.Kind.ToString() ?? "Unknown"}"
        ];
        demotedAssessment = assessment with
        {
            PlacementReady = false,
            RequiresReview = true,
            Decision = WallEvidenceDecision.Review,
            Evidence = AppendEvidence(assessment.Evidence, demotionEvidence)
        };
        return true;
    }

    private static bool IsShortOrFragmentedDimensionLikeLinework(
        WallSegment wall,
        IReadOnlyList<string> evidence,
        ScannerOptions options)
    {
        var shortLength = Math.Max(48.0, Math.Max(options.MinWallLength * 2.0, options.DefaultWallThickness * 8.0));
        if (wall.DrawingLength <= shortLength)
        {
            return true;
        }

        var fragmentedLength = Math.Max(112.0, Math.Max(options.MinWallLength * 4.0, options.DefaultWallThickness * 18.0));
        return wall.DetectionKind == WallDetectionKind.FragmentMerged
            && wall.DrawingLength <= fragmentedLength
            && TryReadRunMergedFragmentCount(evidence, out var fragmentCount)
            && fragmentCount >= 4;
    }

    private static bool TryReadRunMergedFragmentCount(
        IEnumerable<string> evidence,
        out int fragmentCount)
    {
        fragmentCount = 0;
        foreach (var item in evidence)
        {
            if (!item.Contains("run merged", StringComparison.OrdinalIgnoreCase)
                || !item.Contains("fragment", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = item.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < parts.Length; index++)
            {
                if (string.Equals(parts[index], "merged", StringComparison.OrdinalIgnoreCase)
                    && index + 1 < parts.Length
                    && int.TryParse(
                        parts[index + 1],
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    fragmentCount = parsed;
                    return true;
                }
            }
        }

        return false;
    }

    private static double DenseLocalDetailDemotionMaxWallLength(ScannerOptions options) =>
        Math.Max(72.0, Math.Max(options.MinWallLength * 3.0, options.DefaultWallThickness * 12.0));

    private static double DenseLocalDetailNeighborhoodRadius(ScannerOptions options) =>
        Math.Max(54.0, Math.Max(options.MinWallLength * 2.5, options.DefaultWallThickness * 10.0));

    private static bool IsDenseLocalDetailNeighborhood(
        WallSegment wall,
        IReadOnlyList<WallSegment> walls,
        ScannerOptions options,
        out int nearbyCount,
        out int shortNearbyCount,
        out int offAxisNearbyCount)
    {
        var searchBounds = wall.Bounds.Inflate(DenseLocalDetailNeighborhoodRadius(options));
        var maxShortLength = DenseLocalDetailDemotionMaxWallLength(options);
        nearbyCount = 0;
        shortNearbyCount = 0;
        offAxisNearbyCount = 0;

        foreach (var candidate in walls)
        {
            if (string.Equals(candidate.Id, wall.Id, StringComparison.Ordinal)
                || candidate.PageNumber != wall.PageNumber
                || !candidate.Bounds.Intersects(searchBounds))
            {
                continue;
            }

            nearbyCount++;
            if (candidate.DrawingLength <= maxShortLength)
            {
                shortNearbyCount++;
            }

            if (IsOffAxisDetailLine(candidate.CenterLine))
            {
                offAxisNearbyCount++;
            }
        }

        return nearbyCount >= 7
            && shortNearbyCount >= 4
            && (offAxisNearbyCount >= 2 || shortNearbyCount >= 7);
    }

    private static bool IsOffAxisDetailLine(PlanLineSegment line)
    {
        var dx = Math.Abs(line.End.X - line.Start.X);
        var dy = Math.Abs(line.End.Y - line.Start.Y);
        var length = Math.Max(line.Length, 0.001);
        return dx / length > 0.08 && dy / length > 0.08;
    }

    private static bool IsStronglyOffAxisWallLine(PlanLineSegment line) =>
        OffAxisRatio(line) >= 0.16;

    private static double OffAxisRatio(PlanLineSegment line)
    {
        var dx = Math.Abs(line.End.X - line.Start.X);
        var dy = Math.Abs(line.End.Y - line.Start.Y);
        var dominant = Math.Max(dx, dy);
        if (dominant <= 0.001)
        {
            return 0;
        }

        return Math.Min(dx, dy) / dominant;
    }

    private static bool IsDimensionLikeWeakLayerEvidence(IEnumerable<string> evidence) =>
        evidence.Any(item =>
            item.Contains("classified Dimension", StringComparison.OrdinalIgnoreCase)
            || item.Contains("dimension-like text", StringComparison.OrdinalIgnoreCase)
            || item.Contains("DimensionOrAnnotation", StringComparison.OrdinalIgnoreCase));

    private static bool HasExplicitWallBodyEvidence(IEnumerable<string> evidence) =>
        evidence.Any(item =>
            item.Contains("parallel wall-face pair", StringComparison.OrdinalIgnoreCase)
            || item.Contains("strong double-edge wall body", StringComparison.OrdinalIgnoreCase)
            || item.Contains("recovered wall body", StringComparison.OrdinalIgnoreCase)
            || item.Contains("explicit room boundary support", StringComparison.OrdinalIgnoreCase)
            || item.Contains("geometric room boundary support", StringComparison.OrdinalIgnoreCase));

    private static bool IsRetainedByExteriorShellContinuity(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        ScannerOptions options)
    {
        if (!assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.Decision != WallEvidenceDecision.Accept
            || wall.WallType != WallType.Exterior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair)
        {
            return false;
        }

        return IsSevereFragmentedPairDemotionCandidate(
            wall,
            wall.Evidence.Concat(assessment.Evidence),
            options);
    }

    private static bool HasPlacementContextSupport(
        WallGraphComponent? component,
        int roomReferenceCount,
        bool isSharedByRoomAdjacency,
        RoomSideEvidence sideEvidence,
        int supportedTopologyEndpointCount)
    {
        if (roomReferenceCount > 0
            || isSharedByRoomAdjacency
            || sideEvidence.PositiveRoomHits + sideEvidence.NegativeRoomHits > 0
            || supportedTopologyEndpointCount >= 2)
        {
            return true;
        }

        return component is not null
            && component.Kind == WallGraphComponentKind.MainStructural
            && component.WallIds.Count >= 4
            && component.DrawingLength >= 240;
    }

    private static bool ShouldAddExplicitRoomBoundarySupportEvidence(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        WallGraphComponent? component,
        int roomReferenceCount,
        bool hasOutdoorRoomReference)
    {
        if (roomReferenceCount < 1
            || hasOutdoorRoomReference
            || wall.WallType != WallType.Interior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || component is null
            || component.Kind != WallGraphComponentKind.MainStructural
            || component.ExcludedFromStructuralTopology)
        {
            return false;
        }

        if (!assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject)
        {
            return false;
        }

        if (assessment.Category is not (WallEvidenceCategory.StrongWallBody or WallEvidenceCategory.MediumWallBody))
        {
            return false;
        }

        return !HasRoomConfirmedPromotionBlocker(wall, assessment);
    }

    private static bool IsTrustedShortStructuralReturnWall(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        ScannerOptions options)
    {
        if (wall.DrawingLength > 90
            || assessment.Category != WallEvidenceCategory.MediumWallBody
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair)
        {
            return false;
        }

        var evidence = assessment.Evidence
            .Concat(wall.Evidence)
            .ToArray();
        if (!evidence.Any(item => item.Contains("short paired wall evidence", StringComparison.OrdinalIgnoreCase))
            || !evidence.Any(item => item.Contains("only one structurally supported endpoint", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (HasNoisyShortReturnPairEvidence(wall, evidence, options))
        {
            return false;
        }

        return TryReadPairScore(evidence, out var pairScore)
            && pairScore >= 0.82;
    }

    private static bool HasNoisyShortReturnPairEvidence(
        WallSegment wall,
        IReadOnlyList<string> evidence,
        ScannerOptions options)
    {
        if (wall.PairEvidence is { } pairEvidence
            && pairEvidence.FirstFaceFragmentCount + pairEvidence.SecondFaceFragmentCount >= 10)
        {
            return true;
        }

        if (!TryReadMaxHealedFaceGap(evidence, out var maxHealedGap))
        {
            return false;
        }

        var localThickness = wall.Thickness > 0
            ? wall.Thickness
            : options.DefaultWallThickness;
        var maxTrustedGap = Math.Max(
            Math.Max(options.WallSnapTolerance * 1.5, 2.5),
            Math.Min(localThickness * 0.9, wall.DrawingLength * 0.12));
        return maxHealedGap > maxTrustedGap;
    }

    private static bool TryReadMaxHealedFaceGap(
        IEnumerable<string> evidence,
        out double maxHealedGap)
    {
        maxHealedGap = 0;
        foreach (var item in evidence)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            const string marker = "max gap";
            var index = item.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var valueStart = index + marker.Length;
            while (valueStart < item.Length
                && (char.IsWhiteSpace(item[valueStart])
                    || item[valueStart] == ':'
                    || item[valueStart] == '='))
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
                    out var parsed))
            {
                maxHealedGap = Math.Max(maxHealedGap, parsed);
            }
        }

        return maxHealedGap > 0;
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

    private static bool TryReadFaceFragmentCounts(
        IEnumerable<string> evidence,
        out FaceFragmentCounts faceFragments)
    {
        var first = 0;
        var second = 0;
        foreach (var item in evidence)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            if (item.Contains("first face merged", StringComparison.OrdinalIgnoreCase)
                && TryReadIntegerBeforeMarker(item, "fragments", out var firstCount))
            {
                first = Math.Max(first, firstCount);
            }

            if (item.Contains("second face merged", StringComparison.OrdinalIgnoreCase)
                && TryReadIntegerBeforeMarker(item, "fragments", out var secondCount))
            {
                second = Math.Max(second, secondCount);
            }
        }

        faceFragments = new FaceFragmentCounts(first, second);
        return faceFragments.TotalFaceFragmentCount > 0;
    }

    private static bool TryReadIntegerBeforeMarker(
        string value,
        string marker,
        out int parsed)
    {
        parsed = 0;
        var markerIndex = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
        {
            return false;
        }

        var end = markerIndex - 1;
        while (end >= 0 && char.IsWhiteSpace(value[end]))
        {
            end--;
        }

        var start = end;
        while (start >= 0 && char.IsDigit(value[start]))
        {
            start--;
        }

        if (start == end)
        {
            return false;
        }

        return int.TryParse(
            value[(start + 1)..(end + 1)],
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out parsed);
    }

    private static bool HasUnknownOrWeakLayerEvidence(IEnumerable<string> evidence) =>
        evidence.Any(item => item.Contains("layer (unlayered) classified", StringComparison.OrdinalIgnoreCase)
            || item.Contains("layer evidence: no strong layer", StringComparison.OrdinalIgnoreCase));

    private static bool IsFragmentedPairEvidence(IEnumerable<string> evidence) =>
        evidence.Any(item => item.Contains("face merged", StringComparison.OrdinalIgnoreCase)
            || item.Contains("fragmented-face evidence", StringComparison.OrdinalIgnoreCase));

    private static bool IsSevereFragmentedPairDemotionCandidate(
        WallSegment wall,
        IEnumerable<string> evidence,
        ScannerOptions options)
    {
        var evidenceArray = evidence.ToArray();
        if (!IsFragmentedPairEvidence(evidenceArray)
            || !TryReadPairScore(evidenceArray, out var pairScore)
            || !TryReadFaceFragmentCounts(evidenceArray, out var faceFragments))
        {
            return false;
        }

        var maxTrustedLength = Math.Max(90, options.MinWallLength * 4.0);
        return (pairScore < 0.68
            && wall.DrawingLength <= maxTrustedLength
            && faceFragments.MaxFaceFragmentCount >= 40
            && faceFragments.TotalFaceFragmentCount >= 50)
            || (pairScore < 0.95
            && wall.DrawingLength <= Math.Max(180, options.MinWallLength * 7.5)
            && faceFragments.MaxFaceFragmentCount >= 70
            && faceFragments.TotalFaceFragmentCount >= 80);
    }

    private static bool HasRoomConfirmedPromotionBlocker(
        WallSegment wall,
        WallEvidenceWallAssessment assessment) =>
        assessment.Evidence
            .Concat(wall.Evidence)
            .Any(IsRoomConfirmedPromotionBlockerEvidence);

    private static bool IsRoomConfirmedPromotionBlockerEvidence(string evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return false;
        }

        return evidence.Contains("recovered duplicate wall body", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("already represented", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("rejected as non-wall", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("dense local detail", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("dimension-like weak layer True", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("object/fixture detail", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("door/opening", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasWallBodyEvidence(WallSegment wall, WallEvidenceWallAssessment assessment)
    {
        if (wall.DetectionKind == WallDetectionKind.ParallelLinePair
            || assessment.Category == WallEvidenceCategory.RecoveredWallBody)
        {
            return true;
        }

        return assessment.Evidence
            .Concat(wall.Evidence)
            .Any(item => item.Contains("parallel wall-face pair", StringComparison.OrdinalIgnoreCase)
                || item.Contains("recovered by wall evidence map", StringComparison.OrdinalIgnoreCase));
    }

    private static RoomSideEvidence AnalyzeRoomSides(
        WallSegment wall,
        IReadOnlyList<RoomRegion> pageRooms,
        ScannerOptions options)
    {
        if (pageRooms.Count == 0 || wall.CenterLine.Length <= double.Epsilon)
        {
            return RoomSideEvidence.Empty;
        }

        var along = wall.CenterLine.Vector.Normalize();
        if (along.Length <= double.Epsilon)
        {
            return RoomSideEvidence.Empty;
        }

        var normal = new PlanVector(-along.Y, along.X).Normalize();
        var sampleOffset = Math.Max(
            wall.Thickness > 0 ? wall.Thickness * 1.5 : options.DefaultWallThickness * 2.5,
            Math.Max(options.WallSnapTolerance * 3.0, options.DefaultWallThickness * 2.5));
        var positiveHits = 0;
        var negativeHits = 0;
        var positiveOutdoorHits = 0;
        var negativeOutdoorHits = 0;

        foreach (var t in new[] { 0.25, 0.5, 0.75 })
        {
            var point = wall.CenterLine.PointAt(t);
            var positiveRooms = RoomsContaining(point + (normal * sampleOffset), pageRooms, options.WallSnapTolerance);
            if (positiveRooms.Count > 0)
            {
                positiveHits++;
                if (positiveRooms.Any(room => room.UseKind == RoomUseKind.Outdoor))
                {
                    positiveOutdoorHits++;
                }
            }

            var negativeRooms = RoomsContaining(point + (normal * -sampleOffset), pageRooms, options.WallSnapTolerance);
            if (negativeRooms.Count > 0)
            {
                negativeHits++;
                if (negativeRooms.Any(room => room.UseKind == RoomUseKind.Outdoor))
                {
                    negativeOutdoorHits++;
                }
            }
        }

        return new RoomSideEvidence(positiveHits, negativeHits, positiveOutdoorHits, negativeOutdoorHits);
    }

    private static IReadOnlyList<RoomRegion> RoomsContaining(
        PlanPoint point,
        IReadOnlyList<RoomRegion> rooms,
        double tolerance) =>
        rooms
            .Where(room => IsInsideRoom(point, room, tolerance))
            .ToArray();

    private static bool IsInsideRoom(PlanPoint point, RoomRegion room, double tolerance)
    {
        if (!room.Bounds.Contains(point, tolerance))
        {
            return false;
        }

        return room.Boundary.Count < 3 || IsPointInPolygon(point, room.Boundary);
    }

    private static bool IsPointInPolygon(PlanPoint point, IReadOnlyList<PlanPoint> polygon)
    {
        var inside = false;
        for (int index = 0, previous = polygon.Count - 1; index < polygon.Count; previous = index++)
        {
            var currentPoint = polygon[index];
            var previousPoint = polygon[previous];
            var crossesY = currentPoint.Y > point.Y != previousPoint.Y > point.Y;
            if (!crossesY)
            {
                continue;
            }

            var intersectionX = ((previousPoint.X - currentPoint.X) * (point.Y - currentPoint.Y)
                / (previousPoint.Y - currentPoint.Y))
                + currentPoint.X;
            if (point.X < intersectionX)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static IReadOnlyList<string> AppendEvidence(
        IReadOnlyList<string> evidence,
        string refinementEvidence) =>
        evidence
            .Append(refinementEvidence)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> AppendEvidence(
        IReadOnlyList<string> evidence,
        IEnumerable<string> refinementEvidence) =>
        evidence
            .Concat(refinementEvidence)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static bool IsActionableEvidence(string evidence) =>
        !evidence.Contains("unchanged", StringComparison.OrdinalIgnoreCase)
        && !evidence.Contains("inconclusive", StringComparison.OrdinalIgnoreCase);

    private static void AddDiagnostics(
        ScanContext context,
        int changed,
        int evidenceUpdated,
        int roomReferenced,
        int twoSidedRoomEvidence,
        int oneSidedRoomEvidence,
        int rejectedEvidenceProtected,
        int roomConfirmedPlacementPromoted,
        int topologySupportedFragmentedPairPromoted,
        int fragmentedPairPlacementDemoted,
        int denseLocalDetailPlacementDemoted,
        int nonOrthogonalDimensionLikePlacementDemoted,
        int shortDimensionLikePlacementDemoted,
        int fragmentedExteriorShellContinuityRetained,
        int geometricRoomBoundaryEvidenceAdded,
        int explicitRoomBoundaryEvidenceAdded,
        int geometricRoomBoundaryReferencedWallCount,
        int geometricRoomBoundaryReferenceCount)
    {
        var exterior = context.Walls.Count(wall => wall.WallType == WallType.Exterior);
        var interior = context.Walls.Count(wall => wall.WallType == WallType.Interior);
        var unknown = context.Walls.Count(wall => wall.WallType == WallType.Unknown);
        context.AddDiagnostic(
            "walls.architectural_type_refined",
            DiagnosticSeverity.Info,
            StageName,
            $"Refined wall type classifications for {changed} wall(s).",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            properties: new Dictionary<string, string>
            {
                ["wallCount"] = context.Walls.Count.ToString(),
                ["changedWallTypeCount"] = changed.ToString(),
                ["evidenceUpdatedWallCount"] = evidenceUpdated.ToString(),
                ["roomReferencedWallCount"] = roomReferenced.ToString(),
                ["geometricRoomBoundaryReferencedWallCount"] = geometricRoomBoundaryReferencedWallCount.ToString(),
                ["geometricRoomBoundaryReferenceCount"] = geometricRoomBoundaryReferenceCount.ToString(),
                ["geometricRoomBoundaryEvidenceAddedWallCount"] = geometricRoomBoundaryEvidenceAdded.ToString(),
                ["twoSidedRoomEvidenceWallCount"] = twoSidedRoomEvidence.ToString(),
                ["oneSidedRoomEvidenceWallCount"] = oneSidedRoomEvidence.ToString(),
                ["rejectedEvidenceProtectedWallCount"] = rejectedEvidenceProtected.ToString(),
                ["roomConfirmedPlacementPromotedWallCount"] = roomConfirmedPlacementPromoted.ToString(),
                ["topologySupportedFragmentedPairPromotedWallCount"] = topologySupportedFragmentedPairPromoted.ToString(),
                ["fragmentedPairPlacementDemotedWallCount"] = fragmentedPairPlacementDemoted.ToString(),
                ["denseLocalDetailPlacementDemotedWallCount"] = denseLocalDetailPlacementDemoted.ToString(),
                ["nonOrthogonalDimensionLikePlacementDemotedWallCount"] = nonOrthogonalDimensionLikePlacementDemoted.ToString(),
                ["shortDimensionLikePlacementDemotedWallCount"] = shortDimensionLikePlacementDemoted.ToString(),
                ["fragmentedExteriorShellContinuityRetainedWallCount"] = fragmentedExteriorShellContinuityRetained.ToString(),
                ["explicitRoomBoundaryEvidenceAddedWallCount"] = explicitRoomBoundaryEvidenceAdded.ToString(),
                ["exteriorWallCount"] = exterior.ToString(),
                ["interiorWallCount"] = interior.ToString(),
                ["unknownWallCount"] = unknown.ToString()
            });
    }

    private readonly record struct RoomSideEvidence(
        int PositiveRoomHits,
        int NegativeRoomHits,
        int PositiveOutdoorRoomHits,
        int NegativeOutdoorRoomHits)
    {
        public static RoomSideEvidence Empty { get; } = new(0, 0, 0, 0);

        public bool HasRoomsOnBothSides => PositiveRoomHits > 0 && NegativeRoomHits > 0;

        public bool HasRoomsOnExactlyOneSide => PositiveRoomHits > 0 != NegativeRoomHits > 0;

        public bool HasOutdoorRoomSide => PositiveOutdoorRoomHits > 0 || NegativeOutdoorRoomHits > 0;
    }

    private readonly record struct FaceFragmentCounts(int FirstFaceFragmentCount, int SecondFaceFragmentCount)
    {
        public int MaxFaceFragmentCount => Math.Max(FirstFaceFragmentCount, SecondFaceFragmentCount);

        public int TotalFaceFragmentCount => FirstFaceFragmentCount + SecondFaceFragmentCount;
    }

    private enum AxisOrientation
    {
        Unknown,
        Horizontal,
        Vertical
    }

    private sealed record WallTypeRefinement(WallType WallType, string Evidence);
}
