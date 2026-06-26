using System.Globalization;

namespace OpenPlanTrace;

internal sealed class WallTypeRefinementStage : IPipelineStage
{
    private const string StageName = "wall-type-refinement";
    private const double MinTrustedDimensionLikeDenseRoomBoundaryPairScore = 0.80;
    private const double MinSecondaryTrustedDimensionLikeDenseRoomBoundaryLength = 32.0;
    private const double MinMainStructuralOneEndpointDenseRoomBoundaryLength = 42.0;
    private const double MaxMainStructuralOneEndpointDenseRoomBoundaryLength = 96.0;
    private const double MinMainStructuralOneEndpointDenseRoomBoundaryPairScore = 0.94;
    private const int MinMainStructuralOneEndpointDenseRoomBoundarySideRoomHits = 6;
    private const int MaxMainStructuralOneEndpointDenseRoomBoundaryFaceFragments = 12;
    private const int MaxMainStructuralOneEndpointDenseRoomBoundaryTotalFaceFragments = 20;
    private const int MaxTrustedDimensionLikeDenseRoomBoundaryFaceFragments = 32;
    private const int MaxTrustedDimensionLikeDenseRoomBoundaryTotalFaceFragments = 48;

    public string Name => StageName;

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        if (context.Walls.Count == 0 && context.WallCandidates.Count == 0 && context.Rooms.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        var sharedWallIds = BuildSharedWallIds(context.RoomAdjacencyGraph);
        var componentsByWallId = BuildComponentsByWallId(context.WallGraph);
        var supportedTopologyEndpointCountsByWallId = BuildSupportedTopologyEndpointCounts(context.WallGraph);
        var evidenceByWallId = BuildEvidenceByWallId(context.WallEvidenceMap);
        var rejectedEvidenceByWallId = BuildRejectedEvidenceByWallId(context.WallEvidenceMap);
        var roomBoundaryRepairCandidateWalls = context.Walls
            .Concat(context.WallCandidates)
            .Where(wall => !string.IsNullOrWhiteSpace(wall.Id))
            .GroupBy(wall => wall.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var roomWallReferences = RoomBoundaryWallReferenceBuilder.Build(
            context.Rooms,
            context.Walls,
            context.Options.WallSnapTolerance);
        var roomBoundaryRepairSupport = RoomBoundaryRepairSupportBuilder.Build(
            context.Rooms,
            context.Walls,
            roomBoundaryRepairCandidateWalls,
            evidenceByWallId,
            context.Options);
        var exteriorShellRepairSupport = ExteriorShellRepairSupportBuilder.Build(
            context.Walls,
            roomBoundaryRepairCandidateWalls,
            context.Rooms,
            context.SheetRegions,
            evidenceByWallId,
            context.Options);
        var roomIdsByWallId = roomWallReferences.RoomIdsByWallId;
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
        var roomBoundaryRepairPlacementPromoted = 0;
        var roomBoundaryRepairInteriorRefined = 0;
        var roomBoundaryRepairRejectedRecovered = 0;
        var exteriorShellRepairPlacementPromoted = 0;
        var exteriorShellRepairRejectedRecovered = 0;
        var sharedRoomBoundaryGapInferred = 0;
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
            var hasRoomBoundaryRepairSupport = roomBoundaryRepairSupport.SupportByWallId.TryGetValue(
                wall.Id,
                out var repairSupport);
            var hasExteriorShellRepairSupport = exteriorShellRepairSupport.SupportByWallId.TryGetValue(
                wall.Id,
                out var shellRepairSupport);
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
                    hasGeometricRoomBoundarySupport,
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
                && hasRoomBoundaryRepairSupport
                && TryPromoteRoomBoundaryRepairWallEvidence(
                    updatedWall,
                    assessment,
                    component,
                    supportedTopologyEndpointCountsByWallId.TryGetValue(wall.Id, out var repairSupportedEndpointCount)
                        ? repairSupportedEndpointCount
                        : 0,
                    hasOutdoorRoomReference,
                    sideEvidence,
                    repairSupport,
                    context.Options,
                    out var repairPromotedAssessment,
                    out var repairPromotionEvidence))
            {
                if (updatedWall.WallType == WallType.Unknown)
                {
                    var typeEvidence = new[]
                    {
                        "wall type refined interior: iterative room-boundary repair matched an unsupported indoor room edge"
                    };
                    repairPromotedAssessment = repairPromotedAssessment with
                    {
                        Evidence = AppendEvidence(repairPromotedAssessment.Evidence, typeEvidence)
                    };
                    repairPromotionEvidence = repairPromotionEvidence.Concat(typeEvidence).ToArray();
                    updatedWall = updatedWall with { WallType = WallType.Interior };
                    changed++;
                    roomBoundaryRepairInteriorRefined++;
                }

                updatedAssessmentsByWallId[wall.Id] = repairPromotedAssessment;
                updatedWall = updatedWall with
                {
                    Evidence = AppendEvidence(updatedWall.Evidence, repairPromotionEvidence)
                };
                roomBoundaryRepairPlacementPromoted++;
                evidenceUpdated++;
                assessment = repairPromotedAssessment;
            }

            if (assessment is not null
                && hasExteriorShellRepairSupport
                && TryPromoteExteriorShellRepairWallEvidence(
                    updatedWall,
                    assessment,
                    shellRepairSupport,
                    out var shellRepairPromotedAssessment,
                    out var shellRepairPromotionEvidence))
            {
                if (updatedWall.WallType != WallType.Exterior)
                {
                    var typeEvidence = new[]
                    {
                        "wall type refined exterior: global exterior-shell repair matched a trusted shell extension"
                    };
                    shellRepairPromotedAssessment = shellRepairPromotedAssessment with
                    {
                        Evidence = AppendEvidence(shellRepairPromotedAssessment.Evidence, typeEvidence)
                    };
                    shellRepairPromotionEvidence = shellRepairPromotionEvidence.Concat(typeEvidence).ToArray();
                    updatedWall = updatedWall with { WallType = WallType.Exterior };
                    changed++;
                }

                updatedAssessmentsByWallId[wall.Id] = shellRepairPromotedAssessment;
                updatedWall = updatedWall with
                {
                    Evidence = AppendEvidence(updatedWall.Evidence, shellRepairPromotionEvidence)
                };
                exteriorShellRepairPlacementPromoted++;
                evidenceUpdated++;
                assessment = shellRepairPromotedAssessment;
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

        if (TryRecoverRejectedExteriorShellWallCandidates(
            context,
            roomBoundaryRepairCandidateWalls,
            exteriorShellRepairSupport,
            evidenceByWallId,
            out var exteriorRecoveredAssessmentsByWallId,
            out var exteriorRecoveredWallCount))
        {
            exteriorShellRepairRejectedRecovered = exteriorRecoveredWallCount;
            context.WallEvidenceMap = context.WallEvidenceMap with
            {
                Segments = context.WallEvidenceMap.Segments
                    .Select(segment => exteriorRecoveredAssessmentsByWallId.TryGetValue(segment.WallId ?? string.Empty, out var updated)
                        ? segment with
                        {
                            Category = updated.Category,
                            Confidence = updated.Confidence,
                            Evidence = AppendEvidence(segment.Evidence, updated.Evidence)
                        }
                        : segment)
                    .ToArray(),
                Bands = context.WallEvidenceMap.Bands
                    .Select(band => exteriorRecoveredAssessmentsByWallId.TryGetValue(band.WallId ?? string.Empty, out var updated)
                        ? band with
                        {
                            Confidence = updated.Confidence,
                            Evidence = AppendEvidence(band.Evidence, updated.Evidence)
                        }
                        : band)
                    .ToArray(),
                WallAssessments = context.WallEvidenceMap.WallAssessments
                    .Select(assessment => exteriorRecoveredAssessmentsByWallId.TryGetValue(assessment.WallId, out var updated)
                        ? updated
                        : assessment)
                    .ToArray()
            };
        }

        if (TryRecoverRejectedRoomBoundaryWallCandidates(
            context,
            roomBoundaryRepairCandidateWalls,
            roomBoundaryRepairSupport,
            evidenceByWallId,
            out var recoveredAssessmentsByWallId,
            out var recoveredWallCount))
        {
            roomBoundaryRepairRejectedRecovered = recoveredWallCount;
            context.WallEvidenceMap = context.WallEvidenceMap with
            {
                Segments = context.WallEvidenceMap.Segments
                    .Select(segment => recoveredAssessmentsByWallId.TryGetValue(segment.WallId ?? string.Empty, out var updated)
                        ? segment with
                        {
                            Category = updated.Category,
                            Confidence = updated.Confidence,
                            Evidence = AppendEvidence(segment.Evidence, updated.Evidence)
                        }
                        : segment)
                    .ToArray(),
                Bands = context.WallEvidenceMap.Bands
                    .Select(band => recoveredAssessmentsByWallId.TryGetValue(band.WallId ?? string.Empty, out var updated)
                        ? band with
                        {
                            Confidence = updated.Confidence,
                            Evidence = AppendEvidence(band.Evidence, updated.Evidence)
                        }
                        : band)
                    .ToArray(),
                WallAssessments = context.WallEvidenceMap.WallAssessments
                    .Select(assessment => recoveredAssessmentsByWallId.TryGetValue(assessment.WallId, out var updated)
                        ? updated
                        : assessment)
                    .ToArray()
            };
        }

        sharedRoomBoundaryGapInferred = InferSharedRoomBoundaryGapWalls(
            context,
            roomBoundaryRepairSupport);
        var sourceBackedExteriorShellClosureInferred = InferSourceBackedExteriorShellClosureWalls(context);
        var exteriorShellGapInferred = InferExteriorShellGapWalls(context);

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
            roomBoundaryRepairSupport.UnsupportedRoomBoundaryEdgeCount,
            roomBoundaryRepairSupport.CandidateWallCount,
            roomBoundaryRepairPlacementPromoted,
            roomBoundaryRepairInteriorRefined,
            roomBoundaryRepairRejectedRecovered,
            exteriorShellRepairSupport.CandidateWallCount,
            exteriorShellRepairPlacementPromoted,
            exteriorShellRepairRejectedRecovered,
            sharedRoomBoundaryGapInferred,
            sourceBackedExteriorShellClosureInferred,
            exteriorShellGapInferred,
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
            if (!IsExteriorShellContinuityCandidate(
                    wall,
                    evidenceByWallId[wall.Id],
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

    private static bool IsExteriorShellContinuityCandidate(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        ScannerOptions options)
    {
        var isSevereFragmentedPair = IsSevereFragmentedPairDemotionCandidate(
            wall,
            wall.Evidence.Concat(assessment.Evidence),
            options);
        if (!assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.Decision != WallEvidenceDecision.Accept
            || wall.WallType != WallType.Exterior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is not { } pair
            || wall.DrawingLength < Math.Max(32.0, options.MinWallLength * 1.5)
            || (!isSevereFragmentedPair && pair.Score < 0.70)
            || pair.OverlapRatio < 0.88
            || pair.FaceSeparation < 1.5
            || pair.FaceSeparation > 24.0)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();
        if (evidence.Any(item =>
                item.Contains("covered-area boundary", StringComparison.OrdinalIgnoreCase)
                || item.Contains("outdoor covered-area", StringComparison.OrdinalIgnoreCase)
                || item.Contains("unpaired outdoor", StringComparison.OrdinalIgnoreCase)
                || item.Contains("covered entry", StringComparison.OrdinalIgnoreCase)
                || item.Contains("covered-entry", StringComparison.OrdinalIgnoreCase)
                || item.Contains("overbygd", StringComparison.OrdinalIgnoreCase)
                || item.Contains("canopy", StringComparison.OrdinalIgnoreCase)
                || item.Contains("railing", StringComparison.OrdinalIgnoreCase)
                || item.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
                || item.Contains("object/fixture", StringComparison.OrdinalIgnoreCase)
                || item.Contains("fixture detail", StringComparison.OrdinalIgnoreCase)
                || item.Contains("stair", StringComparison.OrdinalIgnoreCase)
                || item.Contains("not trusted", StringComparison.OrdinalIgnoreCase)
                || item.Contains("without shell support", StringComparison.OrdinalIgnoreCase)
                || item.Contains("alone is not trusted", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return isSevereFragmentedPair
            || evidence.Any(item =>
            item.Contains("near detected floorplan/wall envelope", StringComparison.OrdinalIgnoreCase)
            || item.Contains("local outer boundary", StringComparison.OrdinalIgnoreCase)
            || item.Contains("wall type exterior", StringComparison.OrdinalIgnoreCase));
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

        if (WallPlacementReadinessEvaluator.IsTrustedLongIsolatedExteriorShellWallBody(
                wall,
                component,
                assessment))
        {
            return new WallTypeRefinement(
                WallType.Exterior,
                "wall type refined exterior: trusted long isolated exterior shell wall body");
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
        bool hasGeometricRoomBoundarySupport,
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
            isSharedByRoomAdjacency,
            supportedTopologyEndpointCount,
            sideEvidence,
            hasGeometricRoomBoundarySupport,
            options);
        var hasGeometricRoomBoundaryPairConfirmation = IsTrustedGeometricRoomBoundaryPairConfirmation(
            wall,
            assessment,
            component,
            roomReferenceCount,
            supportedTopologyEndpointCount,
            sideEvidence,
            hasGeometricRoomBoundarySupport);
        if (hasOutdoorRoomReference
            || (sideEvidence.HasOutdoorRoomSide
                && !hasRoomBoundaryFragmentConfirmation
                && !hasGeometricRoomBoundaryPairConfirmation))
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
            || sideEvidence.HasRoomsOnBothSides
            || hasGeometricRoomBoundaryPairConfirmation;
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
            && !hasRoomBoundaryFragmentConfirmation
            && !hasGeometricRoomBoundaryPairConfirmation)
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
        .Concat(hasGeometricRoomBoundaryPairConfirmation
            ? new[]
            {
                "wall evidence: geometric room boundary support from reliable room-boundary alignment",
                "wall evidence: geometric room-boundary paired wall promoted after room refinement confirmed the candidate is a structural room edge"
            }
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
        bool isSharedByRoomAdjacency,
        int supportedTopologyEndpointCount,
        RoomSideEvidence sideEvidence,
        bool hasGeometricRoomBoundarySupport,
        ScannerOptions options)
    {
        if (wall.DetectionKind != WallDetectionKind.FragmentMerged
            || wall.WallType != WallType.Interior
            || wall.PairEvidence is not null
            || wall.FragmentEvidence is not { RequiresGeometryReview: false } fragmentEvidence
            || assessment.Category != WallEvidenceCategory.MediumWallBody)
        {
            return false;
        }

        var hasRoomContext = roomReferenceCount >= 1 || sideEvidence.HasRoomsOnBothSides;
        if (!hasRoomContext)
        {
            return false;
        }

        var minimumLength = Math.Max(
            72.0,
            Math.Max(options.MinWallLength * 3.5, wall.Thickness * 10.0));
        var allowsDenseTwoSidedLength =
            roomReferenceCount == 0
            && sideEvidence.HasRoomsOnBothSides
            && wall.DrawingLength >= 72.0;
        if (wall.DrawingLength < minimumLength && !allowsDenseTwoSidedLength)
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
        var denseTwoSidedRoomBoundary =
            roomReferenceCount == 0
            && sideEvidence.HasRoomsOnBothSides
            && fragmentCount is >= 2 and <= 48
            && fragmentEvidence.DuplicatePrimitiveCount <= 12
            && fragmentEvidence.GapRatio <= 0.001
            && fragmentEvidence.TotalHealedGap <= 0.001;
        var evidence = assessment.Evidence
            .Concat(wall.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();
        var longGeometricRoomBoundary =
            (roomReferenceCount > 0 || isSharedByRoomAdjacency || sideEvidence.HasRoomsOnBothSides)
            && wall.DrawingLength >= Math.Max(120.0, options.MinWallLength * 5.0)
            && fragmentCount <= 72
            && fragmentEvidence.DuplicatePrimitiveCount <= 8
            && fragmentEvidence.GapRatio <= 0.02
            && fragmentEvidence.TotalHealedGap <= Math.Max(4.0, wall.Thickness * 0.6)
            && (hasGeometricRoomBoundarySupport
                || evidence.Any(item => item.Contains("geometric room boundary support", StringComparison.OrdinalIgnoreCase)));
        if (fragmentCount < 2
            || (fragmentCount > 8 && !denseTwoSidedRoomBoundary && !longGeometricRoomBoundary)
            || (fragmentEvidence.DuplicatePrimitiveCount > 3 && !cleanDuplicatedRoomBoundary && !denseTwoSidedRoomBoundary)
            || (fragmentEvidence.GapRatio > 0.01 && !longGeometricRoomBoundary)
            || (fragmentEvidence.TotalHealedGap > Math.Max(2.0, wall.Thickness * 0.35) && !longGeometricRoomBoundary))
        {
            return false;
        }

        if (!longGeometricRoomBoundary
            && !evidence.Any(item => item.Contains("only one trusted structural endpoint", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (denseTwoSidedRoomBoundary
            && evidence.Any(item =>
                item.Contains("outdoor", StringComparison.OrdinalIgnoreCase)
                || item.Contains("terrace", StringComparison.OrdinalIgnoreCase)
                || item.Contains("covered-area", StringComparison.OrdinalIgnoreCase)
                || item.Contains("covered entry", StringComparison.OrdinalIgnoreCase)
                || item.Contains("covered-entry", StringComparison.OrdinalIgnoreCase)
                || item.Contains("overbygd", StringComparison.OrdinalIgnoreCase)
                || item.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
                || item.Contains("object/fixture", StringComparison.OrdinalIgnoreCase)
                || item.Contains("fixture detail", StringComparison.OrdinalIgnoreCase)
                || item.Contains("door/opening", StringComparison.OrdinalIgnoreCase)
                || item.Contains("stair", StringComparison.OrdinalIgnoreCase)
                || item.Contains("railing", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return supportedTopologyEndpointCount > 0
            || sideEvidence.PositiveRoomHits + sideEvidence.NegativeRoomHits > 0
            || evidence.Any(item =>
                item.Contains("supported wall evidence inside exterior envelope", StringComparison.OrdinalIgnoreCase)
                || item.Contains("structural context", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTrustedGeometricRoomBoundaryPairConfirmation(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        WallGraphComponent? component,
        int roomReferenceCount,
        int supportedTopologyEndpointCount,
        RoomSideEvidence sideEvidence,
        bool hasGeometricRoomBoundarySupport)
    {
        if (!hasGeometricRoomBoundarySupport
            || wall.WallType != WallType.Interior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is not { } pair
            || wall.FragmentEvidence?.RequiresGeometryReview == true
            || component is null
            || component.ExcludedFromStructuralTopology
            || component.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment
            || assessment.Category != WallEvidenceCategory.MediumWallBody
            || supportedTopologyEndpointCount < 1)
        {
            return false;
        }

        var roomSideHits = sideEvidence.PositiveRoomHits + sideEvidence.NegativeRoomHits;
        if (roomReferenceCount < 1 && roomSideHits < 2)
        {
            return false;
        }

        var evidence = assessment.Evidence
            .Concat(wall.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component.Evidence)
            .ToArray();
        if (!HasWallBodyEvidence(wall, assessment)
            || !evidence.Any(item => item.Contains("supported wall evidence inside exterior envelope", StringComparison.OrdinalIgnoreCase))
            || ContainsAnyBlockingGeometricRoomBoundaryPromotionEvidence(evidence))
        {
            return false;
        }

        var pairScore = wall.PairEvidence?.Score
            ?? (TryReadPairScore(evidence, out var parsedPairScore) ? parsedPairScore : 0);
        if (pairScore < 0.60 || pair.OverlapRatio < 0.90)
        {
            return false;
        }

        var maxFaceFragments = pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount > 0
            ? Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount)
            : TryReadFaceFragmentCounts(evidence, out var parsedFaceFragments)
                ? parsedFaceFragments.MaxFaceFragmentCount
                : 0;
        var totalFaceFragments = pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount > 0
            ? pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount
            : TryReadFaceFragmentCounts(evidence, out var parsedTotalFaceFragments)
                ? parsedTotalFaceFragments.TotalFaceFragmentCount
                : 0;
        if (maxFaceFragments > 144 || totalFaceFragments > 180)
        {
            return false;
        }

        if (IsDimensionLikeWeakLayerEvidence(evidence)
            && (supportedTopologyEndpointCount < 2 || pairScore < MinTrustedDimensionLikeDenseRoomBoundaryPairScore))
        {
            return false;
        }

        return true;
    }

    private static bool ContainsAnyBlockingGeometricRoomBoundaryPromotionEvidence(
        IEnumerable<string> evidence) =>
        evidence.Any(item =>
            item.Contains("outdoor", StringComparison.OrdinalIgnoreCase)
            || item.Contains("terrace", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered-area", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered entry", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered-entry", StringComparison.OrdinalIgnoreCase)
            || item.Contains("overbygd", StringComparison.OrdinalIgnoreCase)
            || item.Contains("canopy", StringComparison.OrdinalIgnoreCase)
            || item.Contains("railing", StringComparison.OrdinalIgnoreCase)
            || item.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
            || item.Contains("object/fixture", StringComparison.OrdinalIgnoreCase)
            || item.Contains("fixture detail", StringComparison.OrdinalIgnoreCase)
            || item.Contains("repeated short detail", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door/opening", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door swing", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door leaf", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door arc", StringComparison.OrdinalIgnoreCase)
            || item.Contains("stair", StringComparison.OrdinalIgnoreCase)
            || item.Contains("not trusted", StringComparison.OrdinalIgnoreCase)
            || item.Contains("without shell support", StringComparison.OrdinalIgnoreCase)
            || item.Contains("alone is not trusted", StringComparison.OrdinalIgnoreCase));

    private static bool TryPromoteRoomBoundaryRepairWallEvidence(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        WallGraphComponent? component,
        int supportedTopologyEndpointCount,
        bool hasOutdoorRoomReference,
        RoomSideEvidence sideEvidence,
        RoomBoundaryRepairSupport? repairSupport,
        ScannerOptions options,
        out WallEvidenceWallAssessment promotedAssessment,
        out IReadOnlyList<string> promotionEvidence)
    {
        promotedAssessment = assessment;
        promotionEvidence = Array.Empty<string>();

        if (repairSupport is null
            || assessment.PlacementReady
            || !assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || wall.WallType == WallType.Exterior
            || hasOutdoorRoomReference
            || sideEvidence.HasOutdoorRoomSide
            || !IsStructuralWallComponent(component)
            || assessment.Category is not (WallEvidenceCategory.StrongWallBody
                or WallEvidenceCategory.MediumWallBody
                or WallEvidenceCategory.RecoveredWallBody)
            || wall.FragmentEvidence?.RequiresGeometryReview == true
            || HasRoomBoundaryRepairPromotionBlocker(wall, assessment))
        {
            return false;
        }

        if (wall.DrawingLength < Math.Max(24.0, options.MinWallLength)
            || repairSupport.WallCoverageRatio < 0.52
            || repairSupport.EdgeCoverageRatio < 0.20
            || repairSupport.OverlapLength < Math.Max(18.0, options.MinWallLength * 0.75))
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component?.Evidence ?? Array.Empty<string>())
            .ToArray();
        if (!HasWallBodyEvidence(wall, assessment)
            && wall.DetectionKind != WallDetectionKind.FragmentMerged
            && repairSupport.WallCoverageRatio < 0.78)
        {
            return false;
        }

        var pairScore = wall.PairEvidence?.Score
            ?? (TryReadPairScore(evidence, out var parsedPairScore) ? parsedPairScore : (double?)null);
        if (wall.DetectionKind == WallDetectionKind.ParallelLinePair)
        {
            if (pairScore is null or < 0.55
                || wall.PairEvidence is { OverlapRatio: < 0.78 })
            {
                return false;
            }

            if (pairScore < 0.68
                && supportedTopologyEndpointCount == 0
                && !sideEvidence.HasRoomsOnBothSides
                && repairSupport.WallCoverageRatio < 0.78)
            {
                return false;
            }
        }
        else if (wall.DetectionKind == WallDetectionKind.FragmentMerged)
        {
            if (wall.FragmentEvidence is not { RequiresGeometryReview: false } fragmentEvidence
                || fragmentEvidence.GapRatio > 0.015
                || fragmentEvidence.TotalHealedGap > Math.Max(3.0, wall.Thickness * 0.6))
            {
                return false;
            }
        }
        else if (wall.DetectionKind == WallDetectionKind.SingleLine)
        {
            if (repairSupport.WallCoverageRatio < 0.82
                || supportedTopologyEndpointCount == 0
                || !HasExplicitWallBodyEvidence(evidence))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        if (component is { Kind: WallGraphComponentKind.IsolatedFragment }
            && (pairScore is null or < 0.70
                || repairSupport.WallCoverageRatio < 0.74))
        {
            return false;
        }

        promotionEvidence = repairSupport.Evidence
            .Concat(new[]
            {
                "wall evidence: iterative room-boundary repair promoted review wall after unsupported room edge scan",
                $"wall evidence: room-boundary repair support topology endpoints {supportedTopologyEndpointCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, two-sided room evidence {sideEvidence.HasRoomsOnBothSides.ToString(System.Globalization.CultureInfo.InvariantCulture)}, component {component?.Kind.ToString() ?? "Unknown"}"
            })
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

    private static bool TryPromoteExteriorShellRepairWallEvidence(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        ExteriorShellRepairSupport? repairSupport,
        out WallEvidenceWallAssessment promotedAssessment,
        out IReadOnlyList<string> promotionEvidence)
    {
        promotedAssessment = assessment;
        promotionEvidence = Array.Empty<string>();

        var chainSupportedShellFragment = repairSupport?.SupportKind == "global-envelope-fragment-chain";
        var chainSupportedStructuralStroke =
            chainSupportedShellFragment
            && wall.DetectionKind is WallDetectionKind.FragmentMerged or WallDetectionKind.SingleLine
            && wall.PairEvidence is null;
        var envelopeSupportedReadyPair =
            repairSupport?.SupportKind is "global-room-envelope-edge" or "global-floorplan-envelope-edge"
            && wall.WallType != WallType.Exterior
            && assessment.PlacementReady
            && !assessment.RequiresReview
            && wall.PairEvidence is
            {
                Score: >= 0.86,
                OverlapRatio: >= 0.90
            };
        var minPairScore = chainSupportedShellFragment ? 0.66 : 0.84;
        var minOverlapRatio = chainSupportedShellFragment ? 0.70 : 0.94;

        if (repairSupport is null
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category is not (WallEvidenceCategory.StrongWallBody
                or WallEvidenceCategory.MediumWallBody
                or WallEvidenceCategory.RecoveredWallBody)
            || (!chainSupportedStructuralStroke
                && (wall.DetectionKind != WallDetectionKind.ParallelLinePair
                    || wall.PairEvidence is not { }))
            || (chainSupportedStructuralStroke
                && !IsPromotableExteriorShellStroke(wall, assessment, repairSupport))
            || (wall.PairEvidence is { } pair
                && (pair.Score < minPairScore || pair.OverlapRatio < minOverlapRatio))
            || (wall.FragmentEvidence?.RequiresGeometryReview == true && !chainSupportedStructuralStroke)
            || HasExteriorShellRecoveryBlocker(
                wall,
                assessment,
                allowGraphObjectLikeReclassification: false,
                allowDimensionLikeStructuralShell: chainSupportedShellFragment || envelopeSupportedReadyPair))
        {
            return false;
        }

        var alreadyReadyExterior = wall.WallType == WallType.Exterior
            && assessment.PlacementReady
            && !assessment.RequiresReview;
        if (alreadyReadyExterior
            && !wall.Evidence.Any(item => item.Contains("no clean wall graph topology span", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        promotionEvidence = repairSupport.Evidence
            .Concat(new[]
            {
                "wall evidence: exterior shell repair promoted wall after global shell continuity scan",
                wall.PairEvidence is { } promotedPair
                    ? $"wall evidence: exterior shell repair pair score {promotedPair.Score.ToString("0.###", CultureInfo.InvariantCulture)}, overlap ratio {promotedPair.OverlapRatio.ToString("0.###", CultureInfo.InvariantCulture)}, support kind {repairSupport.SupportKind}"
                    : $"wall evidence: exterior shell repair structural stroke support score {repairSupport.SupportScore.ToString("0.###", CultureInfo.InvariantCulture)}, length {wall.DrawingLength.ToString("0.###", CultureInfo.InvariantCulture)}, support kind {repairSupport.SupportKind}"
            })
            .ToArray();
        var confidenceBoost = wall.PairEvidence is { } confidencePair
            ? confidencePair.Score + 0.04
            : repairSupport.SupportScore;
        promotedAssessment = assessment with
        {
            Category = assessment.Category == WallEvidenceCategory.StrongWallBody
                ? assessment.Category
                : WallEvidenceCategory.MediumWallBody,
            Confidence = new Confidence(Math.Max(assessment.Confidence.Value, Math.Min(0.92, confidenceBoost))),
            PlacementReady = true,
            RequiresReview = false,
            RejectedAsNoise = false,
            Decision = WallEvidenceDecision.Accept,
            Evidence = AppendEvidence(assessment.Evidence, promotionEvidence)
        };
        return true;
    }

    private static bool HasRoomBoundaryRepairPromotionBlocker(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        bool allowGraphObjectLikeReclassification = false) =>
        wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Any(item => IsRoomBoundaryRepairPromotionBlockerEvidence(
                item,
                allowGraphObjectLikeReclassification));

    private static bool IsRoomBoundaryRepairPromotionBlockerEvidence(
        string evidence,
        bool allowGraphObjectLikeReclassification)
    {
        if (allowGraphObjectLikeReclassification
            && (IsGraphObjectLikeReclassificationEvidence(evidence)
                || evidence.Contains("explicit non-wall evidence: ObjectOrFixtureDetail", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return evidence.Contains("outdoor", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("terrace", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("covered-area", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("covered entry", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("covered-entry", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("overbygd", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("canopy", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("railing", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("object/fixture", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("fixture detail", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("ObjectOrFixtureDetail", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("repeated short detail", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("door/opening", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("door swing", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("door leaf", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("door arc", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("stair", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("dimension-like weak layer True", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("already represented", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("recovered duplicate wall body", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("rejected as non-wall", StringComparison.OrdinalIgnoreCase);
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
                sideEvidence,
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
        RoomSideEvidence sideEvidence,
        int supportedTopologyEndpointCount,
        bool hasGeometricRoomBoundarySupport,
        int offAxisNearbyCount)
    {
        if (offAxisNearbyCount > 1
            || wall.WallType != WallType.Interior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || component is null
            || component.ExcludedFromStructuralTopology
            || component.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment
            || !TryReadPairScore(evidence, out var pairScore)
            || !TryReadFaceFragmentCounts(evidence, out var faceFragments)
            || faceFragments.MaxFaceFragmentCount > MaxTrustedDimensionLikeDenseRoomBoundaryFaceFragments
            || faceFragments.TotalFaceFragmentCount > MaxTrustedDimensionLikeDenseRoomBoundaryTotalFaceFragments)
        {
            return false;
        }

        var hasGeometricRoomBoundaryProof =
            hasGeometricRoomBoundarySupport
            && roomReferenceCount >= 1
            && supportedTopologyEndpointCount >= 2
            && pairScore >= MinTrustedDimensionLikeDenseRoomBoundaryPairScore
            && (component.Kind == WallGraphComponentKind.MainStructural
                || wall.DrawingLength >= MinSecondaryTrustedDimensionLikeDenseRoomBoundaryLength);
        var sideRoomHitCount = sideEvidence.PositiveRoomHits + sideEvidence.NegativeRoomHits;
        var pairOverlap = wall.PairEvidence?.OverlapRatio
            ?? (TryReadPairOverlapRatio(evidence, out var parsedOverlapRatio) ? parsedOverlapRatio : 0);
        var hasStrongSideEndpointProof =
            sideRoomHitCount >= 5
            && supportedTopologyEndpointCount >= 2
            && pairScore >= 0.84
            && pairOverlap >= 0.98
            && wall.DrawingLength >= 24.0
            && wall.DrawingLength <= 72.0
            && (component.Kind == WallGraphComponentKind.MainStructural
                || component.Kind == WallGraphComponentKind.SecondaryStructural);
        var hasMainStructuralOneEndpointSideProof =
            component.Kind == WallGraphComponentKind.MainStructural
            && sideRoomHitCount >= MinMainStructuralOneEndpointDenseRoomBoundarySideRoomHits
            && supportedTopologyEndpointCount >= 1
            && pairScore >= MinMainStructuralOneEndpointDenseRoomBoundaryPairScore
            && pairOverlap >= 0.98
            && wall.DrawingLength >= MinMainStructuralOneEndpointDenseRoomBoundaryLength
            && wall.DrawingLength <= MaxMainStructuralOneEndpointDenseRoomBoundaryLength
            && faceFragments.MaxFaceFragmentCount <= MaxMainStructuralOneEndpointDenseRoomBoundaryFaceFragments
            && faceFragments.TotalFaceFragmentCount <= MaxMainStructuralOneEndpointDenseRoomBoundaryTotalFaceFragments;
        if (!hasGeometricRoomBoundaryProof
            && !hasStrongSideEndpointProof
            && !hasMainStructuralOneEndpointSideProof)
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

        return IsExteriorShellContinuityCandidate(wall, assessment, options);
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

    private static bool TryReadPairOverlapRatio(
        IEnumerable<string> evidence,
        out double overlapRatio)
    {
        foreach (var item in evidence)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            var index = item.IndexOf("overlap ratio", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var valueStart = index + "overlap ratio".Length;
            while (valueStart < item.Length && !char.IsDigit(item[valueStart]) && item[valueStart] != '-' && item[valueStart] != '.')
            {
                valueStart++;
            }

            var valueEnd = valueStart;
            while (valueEnd < item.Length && (char.IsDigit(item[valueEnd]) || item[valueEnd] == ',' || item[valueEnd] == '.'))
            {
                valueEnd++;
            }

            if (valueEnd <= valueStart)
            {
                continue;
            }

            var value = item[valueStart..valueEnd].Replace(',', '.');
            if (double.TryParse(
                    value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out overlapRatio))
            {
                return true;
            }
        }

        overlapRatio = 0;
        return false;
    }

    private static bool TryReadFaceFragmentCounts(
        IEnumerable<string> evidence,
        out FaceFragmentCounts faceFragments)
    {
        var firstMerged = 0;
        var firstCollapsed = 0;
        var secondMerged = 0;
        var secondCollapsed = 0;
        foreach (var item in evidence)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            if (TryReadFaceFragmentCount(
                    item,
                    "first face",
                    out var firstCount,
                    out var firstCountIsMerged))
            {
                if (firstCountIsMerged)
                {
                    firstMerged = Math.Max(firstMerged, firstCount);
                }
                else
                {
                    firstCollapsed = Math.Max(firstCollapsed, firstCount);
                }
            }

            if (TryReadFaceFragmentCount(
                    item,
                    "second face",
                    out var secondCount,
                    out var secondCountIsMerged))
            {
                if (secondCountIsMerged)
                {
                    secondMerged = Math.Max(secondMerged, secondCount);
                }
                else
                {
                    secondCollapsed = Math.Max(secondCollapsed, secondCount);
                }
            }
        }

        var first = firstMerged > 0 ? firstMerged : firstCollapsed;
        var second = secondMerged > 0 ? secondMerged : secondCollapsed;
        faceFragments = new FaceFragmentCounts(first, second);
        return faceFragments.TotalFaceFragmentCount > 0;
    }

    private static bool TryReadFaceFragmentCount(
        string value,
        string faceMarker,
        out int count,
        out bool isMerged)
    {
        count = 0;
        isMerged = false;
        if (!value.Contains(faceMarker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value.Contains("merged", StringComparison.OrdinalIgnoreCase)
            && TryReadIntegerBeforeMarker(value, "fragments", out count))
        {
            isMerged = true;
            return true;
        }

        return value.Contains("collapsed", StringComparison.OrdinalIgnoreCase)
            && TryReadIntegerBeforeMarker(value, "duplicate", out count);
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

    private static bool IsGraphObjectLikeRoomBoundaryFalsePositive(
        WallSegment wall,
        WallEvidenceWallAssessment assessment)
    {
        if (wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is not { } pair
            || pair.Score < 0.82
            || pair.OverlapRatio < 0.90
            || pair.FaceSeparation < 2.0
            || pair.FaceSeparation > 18.0)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();
        return evidence.Any(IsGraphObjectLikeReclassificationEvidence)
            && evidence.Any(item =>
                item.Contains("parallel wall-face pair", StringComparison.OrdinalIgnoreCase)
                || item.Contains("strong double-edge wall body", StringComparison.OrdinalIgnoreCase)
                || item.Contains("supported wall evidence inside exterior envelope", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGraphObjectLikeReclassificationEvidence(string evidence) =>
        evidence.Contains("graph component", StringComparison.OrdinalIgnoreCase)
        && (evidence.Contains("ObjectLikeIsland", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("object-like linework", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("reclassified as object/fixture detail", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("component excluded from structural topology as compact object-like linework", StringComparison.OrdinalIgnoreCase));

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

    private static bool TryRecoverRejectedRoomBoundaryWallCandidates(
        ScanContext context,
        IReadOnlyList<WallSegment> candidateWalls,
        RoomBoundaryRepairSupportResult roomBoundaryRepairSupport,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> evidenceByWallId,
        out Dictionary<string, WallEvidenceWallAssessment> recoveredAssessmentsByWallId,
        out int recoveredWallCount)
    {
        recoveredAssessmentsByWallId = new Dictionary<string, WallEvidenceWallAssessment>(StringComparer.Ordinal);
        recoveredWallCount = 0;
        if (roomBoundaryRepairSupport.SupportByWallId.Count == 0)
        {
            return false;
        }

        var existingWallIds = context.Walls
            .Select(wall => wall.Id)
            .ToHashSet(StringComparer.Ordinal);
        var candidatesByWallId = candidateWalls
            .Where(wall => !string.IsNullOrWhiteSpace(wall.Id))
            .GroupBy(wall => wall.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var roomsByPage = context.Rooms
            .GroupBy(room => room.PageNumber)
            .ToDictionary(group => group.Key, group => group.ToArray());

        foreach (var support in roomBoundaryRepairSupport.SupportByWallId.Values)
        {
            if (existingWallIds.Contains(support.WallId)
                || !candidatesByWallId.TryGetValue(support.WallId, out var candidate)
                || !evidenceByWallId.TryGetValue(support.WallId, out var assessment)
                || !roomsByPage.TryGetValue(candidate.PageNumber, out var pageRooms))
            {
                continue;
            }

            var sideEvidence = AnalyzeRoomSides(candidate, pageRooms, context.Options);
            if (!TryRecoverRejectedRoomBoundaryWallCandidate(
                candidate,
                assessment,
                support,
                sideEvidence,
                context.Options,
                out var recoveredWall,
                out var recoveredAssessment))
            {
                continue;
            }

            context.Walls.Add(recoveredWall);
            existingWallIds.Add(recoveredWall.Id);
            recoveredAssessmentsByWallId[recoveredWall.Id] = recoveredAssessment;
            recoveredWallCount++;
        }

        return recoveredWallCount > 0;
    }

    private static bool TryRecoverRejectedExteriorShellWallCandidates(
        ScanContext context,
        IReadOnlyList<WallSegment> candidateWalls,
        ExteriorShellRepairSupportResult exteriorShellRepairSupport,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> evidenceByWallId,
        out Dictionary<string, WallEvidenceWallAssessment> recoveredAssessmentsByWallId,
        out int recoveredWallCount)
    {
        recoveredAssessmentsByWallId = new Dictionary<string, WallEvidenceWallAssessment>(StringComparer.Ordinal);
        recoveredWallCount = 0;
        if (exteriorShellRepairSupport.SupportByWallId.Count == 0)
        {
            return false;
        }

        var existingWallIds = context.Walls
            .Select(wall => wall.Id)
            .ToHashSet(StringComparer.Ordinal);
        var candidatesByWallId = candidateWalls
            .Where(wall => !string.IsNullOrWhiteSpace(wall.Id))
            .GroupBy(wall => wall.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var support in exteriorShellRepairSupport.SupportByWallId.Values)
        {
            if (existingWallIds.Contains(support.WallId)
                || !candidatesByWallId.TryGetValue(support.WallId, out var candidate)
                || !evidenceByWallId.TryGetValue(support.WallId, out var assessment)
                || !TryRecoverRejectedExteriorShellWallCandidate(
                    candidate,
                    assessment,
                    support,
                    out var recoveredWall,
                    out var recoveredAssessment))
            {
                continue;
            }

            context.Walls.Add(recoveredWall);
            existingWallIds.Add(recoveredWall.Id);
            recoveredAssessmentsByWallId[recoveredWall.Id] = recoveredAssessment;
            recoveredWallCount++;
        }

        return recoveredWallCount > 0;
    }

    private static int InferSharedRoomBoundaryGapWalls(
        ScanContext context,
        RoomBoundaryRepairSupportResult roomBoundaryRepairSupport)
    {
        var edges = ReliableIndoorRoomBoundaryEdgesForInference(context.Rooms).ToArray();
        if (edges.Length < 2)
        {
            return 0;
        }

        var inferredWalls = new List<WallSegment>();
        var inferredAssessments = new List<WallEvidenceWallAssessment>();
        var inferredSegments = new List<WallEvidenceSegment>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var inferredByPage = new Dictionary<int, int>();
        var sequenceByPage = new Dictionary<int, int>();
        var maxPerPage = Math.Min(12, Math.Max(2, context.Options.MaxWallEvidenceRecoveredWallsPerPage / 4));

        for (var firstIndex = 0; firstIndex < edges.Length; firstIndex++)
        {
            var first = edges[firstIndex];
            for (var secondIndex = firstIndex + 1; secondIndex < edges.Length; secondIndex++)
            {
                var second = edges[secondIndex];
                if (first.PageNumber != second.PageNumber
                    || string.Equals(first.RoomId, second.RoomId, StringComparison.Ordinal)
                    || first.Orientation != second.Orientation)
                {
                    continue;
                }

                var coordinateTolerance = Math.Max(
                    context.Options.WallSnapTolerance * 3.0,
                    context.Options.DefaultWallThickness * 2.0);
                var axisDistance = Math.Abs(first.Coordinate - second.Coordinate);
                if (axisDistance > coordinateTolerance)
                {
                    continue;
                }

                var overlapStart = Math.Max(first.Start, second.Start);
                var overlapEnd = Math.Min(first.End, second.End);
                var overlapLength = overlapEnd - overlapStart;
                if (overlapLength < Math.Max(54.0, context.Options.MinWallLength * 2.0))
                {
                    continue;
                }

                var lengthRatio = overlapLength / Math.Max(1.0, Math.Min(first.Length, second.Length));
                if (lengthRatio < 0.52)
                {
                    continue;
                }

                var coordinate = (first.Coordinate + second.Coordinate) / 2.0;
                var line = AxisLine(first.Orientation, coordinate, overlapStart, overlapEnd);
                if (IsSharedBoundaryInferenceCovered(line, first.PageNumber, context.Walls.Concat(inferredWalls).ToArray(), context.Options)
                    || RoomBoundaryRepairSupportTouchesLine(roomBoundaryRepairSupport, line, first.PageNumber, context.Options))
                {
                    continue;
                }

                var key = SharedBoundaryInferenceKey(first.PageNumber, first.Orientation, coordinate, overlapStart, overlapEnd);
                if (!seenKeys.Add(key))
                {
                    continue;
                }

                inferredByPage.TryGetValue(first.PageNumber, out var pageCount);
                if (pageCount >= maxPerPage)
                {
                    continue;
                }

                var sequence = sequenceByPage.TryGetValue(first.PageNumber, out var existingSequence)
                    ? existingSequence + 1
                    : 1;
                sequenceByPage[first.PageNumber] = sequence;
                inferredByPage[first.PageNumber] = pageCount + 1;

                var id = $"page:{first.PageNumber}:wall-room-boundary-inferred:{sequence:000}";
                var evidence = new[]
                {
                    "wall evidence: inferred interior wall from unsupported shared indoor room-boundary edge",
                    $"wall evidence: shared room-boundary inference rooms {first.RoomId}/{second.RoomId}, axis distance {axisDistance.ToString("0.###", CultureInfo.InvariantCulture)}, overlap {overlapLength.ToString("0.###", CultureInfo.InvariantCulture)}, overlap ratio {lengthRatio.ToString("0.###", CultureInfo.InvariantCulture)}"
                };
                var wall = new WallSegment(
                    id,
                    first.PageNumber,
                    line,
                    context.Options.DefaultWallThickness,
                    new Confidence(0.68))
                {
                    DetectionKind = WallDetectionKind.SingleLine,
                    WallType = WallType.Interior,
                    Evidence = evidence
                };
                var assessment = new WallEvidenceWallAssessment(
                    wall.Id,
                    wall.PageNumber,
                    wall.Bounds,
                    WallEvidenceCategory.MediumWallBody,
                    new Confidence(0.68),
                    true,
                    false,
                    false,
                    Array.Empty<string>(),
                    evidence)
                {
                    Decision = WallEvidenceDecision.Accept,
                    ScoreBreakdown = new WallEvidenceScoreBreakdown(
                        0.68,
                        0,
                        0.68,
                        0,
                        0,
                        0.46,
                        0.22,
                        0,
                        0,
                        evidence,
                        Array.Empty<string>())
                };

                inferredWalls.Add(wall);
                inferredAssessments.Add(assessment);
                inferredSegments.Add(new WallEvidenceSegment(
                    $"wall-evidence-segment:{wall.Id}",
                    wall.PageNumber,
                    wall.CenterLine,
                    wall.Bounds,
                    assessment.Category,
                    assessment.Confidence,
                    wall.Id,
                    wall.SourcePrimitiveIds,
                    assessment.Evidence));
            }
        }

        if (inferredWalls.Count == 0)
        {
            return 0;
        }

        context.Walls.AddRange(inferredWalls);
        context.WallEvidenceMap = context.WallEvidenceMap with
        {
            Segments = context.WallEvidenceMap.Segments.Concat(inferredSegments).ToArray(),
            WallAssessments = context.WallEvidenceMap.WallAssessments.Concat(inferredAssessments).ToArray(),
            RecoveredCandidateWallCount = context.WallEvidenceMap.RecoveredCandidateWallCount + inferredWalls.Count
        };
        return inferredWalls.Count;
    }

    private static int InferExteriorShellGapWalls(ScanContext context)
    {
        var rooms = context.Rooms.ToArray();
        var indoorRooms = ReliableIndoorRoomsForExteriorShellInference(rooms).ToArray();
        if (indoorRooms.Length == 0)
        {
            return 0;
        }

        var inferredWalls = new List<WallSegment>();
        var inferredAssessments = new List<WallEvidenceWallAssessment>();
        var inferredSegments = new List<WallEvidenceSegment>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var inferredByPage = new Dictionary<int, int>();
        var sequenceByPage = new Dictionary<int, int>();
        var maxPerPage = Math.Min(24, Math.Max(4, context.Options.MaxWallEvidenceRecoveredWallsPerPage / 2));
        var sampleOffset = Math.Max(
            context.Options.WallSnapTolerance * 4.0,
            context.Options.DefaultWallThickness * 2.5);
        var minimumLength = Math.Max(42.0, context.Options.MinWallLength * 1.75);
        var maximumInferredGapLength = Math.Max(
            context.Options.MaxOpeningGap * 1.75,
            context.Options.DefaultWallThickness * 18.0);
        var maximumAnchoredInferredSpanLength = Math.Max(
            maximumInferredGapLength,
            Math.Max(context.Options.MaxOpeningGap * 4.0, context.Options.DefaultWallThickness * 48.0));

        var evidenceByWallId = context.WallEvidenceMap.WallAssessments
            .Where(assessment => !string.IsNullOrWhiteSpace(assessment.WallId))
            .GroupBy(assessment => assessment.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var eligiblePages = BuildExteriorShellInferenceEligiblePages(
            context.Walls,
            evidenceByWallId,
            context.Options);
        if (eligiblePages.Count == 0)
        {
            return 0;
        }

        foreach (var edge in ReliableIndoorExteriorBoundaryEdgesForInference(rooms, indoorRooms, sampleOffset, minimumLength))
        {
            if (!eligiblePages.Contains(edge.PageNumber))
            {
                continue;
            }

            var hasSourceLineSupport = TryFindExteriorShellInferenceSourceLineSupport(
                context,
                edge.PageNumber,
                edge.Line,
                out var sourceLineSupportIds,
                out var sourceLineCoverage);
            var requiresLongSpanSupport = edge.Line.Length > maximumInferredGapLength;
            if (edge.Line.Length > maximumAnchoredInferredSpanLength)
            {
                if (!hasSourceLineSupport)
                {
                    continue;
                }
            }

            if (!HasExteriorShellInferenceAnchor(
                    edge.Line,
                    edge.PageNumber,
                    context.Walls.Concat(inferredWalls).ToArray(),
                    evidenceByWallId,
                    context.Options))
            {
                continue;
            }

            if (requiresLongSpanSupport
                && !hasSourceLineSupport
                && !HasExteriorShellInferenceLongSpanSupport(
                    edge.Line,
                    edge.PageNumber,
                    context.Walls.Concat(inferredWalls).ToArray(),
                    evidenceByWallId,
                    context.Options))
            {
                continue;
            }

            if (IsExteriorShellInferenceCovered(
                    edge.Line,
                    edge.PageNumber,
                    context.Walls.Concat(inferredWalls).ToArray(),
                    evidenceByWallId,
                    context.Options))
            {
                continue;
            }

            var key = ExteriorShellInferenceKey(edge.PageNumber, edge.Orientation, edge.Coordinate, edge.Start, edge.End);
            if (!seenKeys.Add(key))
            {
                continue;
            }

            inferredByPage.TryGetValue(edge.PageNumber, out var pageCount);
            if (pageCount >= maxPerPage)
            {
                continue;
            }

            var sequence = sequenceByPage.TryGetValue(edge.PageNumber, out var existingSequence)
                ? existingSequence + 1
                : 1;
            sequenceByPage[edge.PageNumber] = sequence;
            inferredByPage[edge.PageNumber] = pageCount + 1;

            var id = $"page:{edge.PageNumber}:wall-exterior-shell-inferred:{sequence:000}";
            var evidence = new[]
            {
                "wall evidence: inferred exterior shell wall from indoor room boundary with outside on opposite side",
                $"wall evidence: exterior-shell inference room {edge.RoomId}, outside side {edge.OutsideSide}, overlap length {edge.Line.Length.ToString("0.###", CultureInfo.InvariantCulture)}, sample offset {sampleOffset.ToString("0.###", CultureInfo.InvariantCulture)}"
            }
            .Concat(hasSourceLineSupport
                ? new[]
                {
                    $"wall evidence: exterior-shell inference source-line support coverage {sourceLineCoverage.ToString("0.###", CultureInfo.InvariantCulture)} from {sourceLineSupportIds.Count.ToString(CultureInfo.InvariantCulture)} primitive(s)"
                }
                : Array.Empty<string>())
            .ToArray();
            var wall = new WallSegment(
                id,
                edge.PageNumber,
                edge.Line,
                Math.Max(context.Options.DefaultWallThickness, 4.0),
                new Confidence(0.66))
            {
                DetectionKind = WallDetectionKind.SingleLine,
                WallType = WallType.Exterior,
                SourcePrimitiveIds = sourceLineSupportIds,
                Evidence = evidence
            };
            var assessment = new WallEvidenceWallAssessment(
                wall.Id,
                wall.PageNumber,
                wall.Bounds,
                WallEvidenceCategory.RecoveredWallBody,
                new Confidence(0.66),
                PlacementReady: true,
                RequiresReview: false,
                RejectedAsNoise: false,
                sourceLineSupportIds,
                evidence)
            {
                Decision = WallEvidenceDecision.Accept,
                ScoreBreakdown = new WallEvidenceScoreBreakdown(
                    0.66,
                    0,
                    0.66,
                    0,
                    0,
                    0.44,
                    0.22,
                    0,
                    0,
                    evidence,
                    Array.Empty<string>())
            };

            inferredWalls.Add(wall);
            inferredAssessments.Add(assessment);
            inferredSegments.Add(new WallEvidenceSegment(
                $"wall-evidence-segment:{wall.Id}",
                wall.PageNumber,
                wall.CenterLine,
                wall.Bounds,
                assessment.Category,
                wall.Confidence,
                wall.Id,
                sourceLineSupportIds,
                assessment.Evidence));
        }

        if (inferredWalls.Count == 0)
        {
            return 0;
        }

        context.Walls.AddRange(inferredWalls);
        context.WallEvidenceMap = context.WallEvidenceMap with
        {
            Segments = context.WallEvidenceMap.Segments.Concat(inferredSegments).ToArray(),
            WallAssessments = context.WallEvidenceMap.WallAssessments.Concat(inferredAssessments).ToArray(),
            RecoveredCandidateWallCount = context.WallEvidenceMap.RecoveredCandidateWallCount + inferredWalls.Count
        };
        return inferredWalls.Count;
    }

    private static int InferSourceBackedExteriorShellClosureWalls(ScanContext context)
    {
        var evidenceByWallId = BuildEvidenceByWallId(context.WallEvidenceMap);
        var inferredWalls = new List<WallSegment>();
        var inferredAssessments = new List<WallEvidenceWallAssessment>();
        var inferredSegments = new List<WallEvidenceSegment>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var sequenceByPage = new Dictionary<int, int>();
        var minLength = Math.Max(160.0, context.Options.MinWallLength * 6.0);
        var maxPerPage = Math.Min(12, Math.Max(2, context.Options.MaxWallEvidenceRecoveredWallsPerPage / 4));

        foreach (var page in context.Document.Pages)
        {
            var pageCount = 0;
            var candidates = PrimitiveGeometry
                .EnumerateLines(page, context)
                .Select(line => TryCreateSourceBackedExteriorShellClosureCandidate(line, context.Options))
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!)
                .Where(candidate => candidate.Line.Length >= minLength)
                .GroupBy(candidate => SourceBackedExteriorShellClosureKey(
                    page.Number,
                    candidate.Orientation,
                    candidate.Coordinate,
                    candidate.Start,
                    candidate.End),
                    StringComparer.Ordinal)
                .Select(group => MergeSourceBackedExteriorShellClosureCandidates(group))
                .OrderByDescending(candidate => candidate.Line.Length)
                .ThenBy(candidate => candidate.Coordinate)
                .ThenBy(candidate => candidate.Start)
                .ToArray();

            foreach (var candidate in candidates)
            {
                if (pageCount >= maxPerPage)
                {
                    break;
                }

                if (!seenKeys.Add(SourceBackedExteriorShellClosureKey(
                        page.Number,
                        candidate.Orientation,
                        candidate.Coordinate,
                        candidate.Start,
                        candidate.End)))
                {
                    continue;
                }

                var walls = context.Walls.Concat(inferredWalls).ToArray();
                if (IsSourceBackedExteriorShellClosureCovered(
                        candidate.Line,
                        page.Number,
                        walls,
                        context.Options))
                {
                    continue;
                }

                if (!TryFindSourceBackedExteriorShellClosureAnchors(
                        candidate.Line,
                        page.Number,
                        walls,
                        evidenceByWallId,
                        context.Options,
                        out var anchorWallIds,
                        out var anchorEvidence))
                {
                    continue;
                }

                var sequence = sequenceByPage.TryGetValue(page.Number, out var existingSequence)
                    ? existingSequence + 1
                    : 1;
                sequenceByPage[page.Number] = sequence;
                pageCount++;

                var id = $"page:{page.Number}:wall-exterior-shell-source-backed:{sequence:000}";
                var evidence = new[]
                {
                    "wall evidence: source-backed exterior shell closure recovered from long PDF line with shell anchors",
                    $"wall evidence: source-backed shell closure length {candidate.Line.Length.ToString("0.###", CultureInfo.InvariantCulture)}, anchors {string.Join(",", anchorWallIds)}",
                    $"wall evidence: source-backed shell closure primitive count {candidate.SourcePrimitiveIds.Count.ToString(CultureInfo.InvariantCulture)}"
                }
                .Concat(anchorEvidence)
                .ToArray();
                var wall = new WallSegment(
                    id,
                    page.Number,
                    candidate.Line,
                    Math.Max(context.Options.DefaultWallThickness, 4.0),
                    new Confidence(0.68))
                {
                    DetectionKind = WallDetectionKind.SingleLine,
                    WallType = WallType.Exterior,
                    SourcePrimitiveIds = candidate.SourcePrimitiveIds,
                    Evidence = evidence
                };
                var assessment = new WallEvidenceWallAssessment(
                    wall.Id,
                    wall.PageNumber,
                    wall.Bounds,
                    WallEvidenceCategory.RecoveredWallBody,
                    wall.Confidence,
                    PlacementReady: true,
                    RequiresReview: false,
                    RejectedAsNoise: false,
                    candidate.SourcePrimitiveIds,
                    evidence)
                {
                    Decision = WallEvidenceDecision.Accept,
                    ScoreBreakdown = new WallEvidenceScoreBreakdown(
                        0.68,
                        0,
                        0.68,
                        0,
                        0,
                        0.48,
                        0.20,
                        0,
                        0,
                        evidence,
                        Array.Empty<string>())
                };

                inferredWalls.Add(wall);
                inferredAssessments.Add(assessment);
                inferredSegments.Add(new WallEvidenceSegment(
                    $"wall-evidence-segment:{wall.Id}",
                    wall.PageNumber,
                    wall.CenterLine,
                    wall.Bounds,
                    assessment.Category,
                    wall.Confidence,
                    wall.Id,
                    candidate.SourcePrimitiveIds,
                    assessment.Evidence));
            }
        }

        if (inferredWalls.Count == 0)
        {
            return 0;
        }

        context.Walls.AddRange(inferredWalls);
        context.WallEvidenceMap = context.WallEvidenceMap with
        {
            Segments = context.WallEvidenceMap.Segments.Concat(inferredSegments).ToArray(),
            WallAssessments = context.WallEvidenceMap.WallAssessments.Concat(inferredAssessments).ToArray(),
            RecoveredCandidateWallCount = context.WallEvidenceMap.RecoveredCandidateWallCount + inferredWalls.Count
        };
        return inferredWalls.Count;
    }

    private static SourceBackedExteriorShellClosureCandidate? TryCreateSourceBackedExteriorShellClosureCandidate(
        PrimitiveLine primitiveLine,
        ScannerOptions options)
    {
        if (primitiveLine.Segment.Length < Math.Max(48.0, options.MinWallLength * 2.0)
            || !TryResolveAxisInterval(primitiveLine.Segment, out var orientation, out var coordinate, out var start, out var end))
        {
            return null;
        }

        return new SourceBackedExteriorShellClosureCandidate(
            orientation,
            coordinate,
            start,
            end,
            AxisLine(orientation, coordinate, start, end),
            new[] { primitiveLine.PrimitiveId });
    }

    private static SourceBackedExteriorShellClosureCandidate MergeSourceBackedExteriorShellClosureCandidates(
        IEnumerable<SourceBackedExteriorShellClosureCandidate> candidates)
    {
        var ordered = candidates
            .OrderBy(candidate => candidate.Start)
            .ThenBy(candidate => candidate.End)
            .ToArray();
        var orientation = ordered[0].Orientation;
        var coordinate = ordered.Average(candidate => candidate.Coordinate);
        var start = ordered.Min(candidate => candidate.Start);
        var end = ordered.Max(candidate => candidate.End);
        return new SourceBackedExteriorShellClosureCandidate(
            orientation,
            coordinate,
            start,
            end,
            AxisLine(orientation, coordinate, start, end),
            ordered
                .SelectMany(candidate => candidate.SourcePrimitiveIds)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray());
    }

    private static string SourceBackedExteriorShellClosureKey(
        int pageNumber,
        AxisOrientation orientation,
        double coordinate,
        double start,
        double end) =>
        string.Join(
            ":",
            pageNumber.ToString(CultureInfo.InvariantCulture),
            "SourceBackedExteriorShellClosure",
            orientation.ToString(),
            Math.Round(coordinate / 3.0).ToString(CultureInfo.InvariantCulture),
            Math.Round(start / 6.0).ToString(CultureInfo.InvariantCulture),
            Math.Round(end / 6.0).ToString(CultureInfo.InvariantCulture));

    private static bool IsSourceBackedExteriorShellClosureCovered(
        PlanLineSegment line,
        int pageNumber,
        IReadOnlyList<WallSegment> walls,
        ScannerOptions options) =>
        walls
            .Where(wall => wall.PageNumber == pageNumber)
            .Any(wall =>
                AreNearParallel(line, wall.CenterLine)
                && Math.Max(
                    line.DistanceToPoint(wall.CenterLine.Midpoint),
                    wall.CenterLine.DistanceToPoint(line.Midpoint))
                    <= Math.Max(options.WallSnapTolerance * 4.0, options.DefaultWallThickness * 2.5)
                && AxisAlignedCoverageRatioOfFirst(line, wall.CenterLine) >= 0.72);

    private static bool TryFindSourceBackedExteriorShellClosureAnchors(
        PlanLineSegment line,
        int pageNumber,
        IReadOnlyList<WallSegment> walls,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> evidenceByWallId,
        ScannerOptions options,
        out IReadOnlyList<string> anchorWallIds,
        out IReadOnlyList<string> evidence)
    {
        anchorWallIds = Array.Empty<string>();
        evidence = Array.Empty<string>();
        if (!TryResolveAxisInterval(line, out var orientation, out _, out _, out _))
        {
            return false;
        }

        var tolerance = Math.Max(options.WallSnapTolerance * 5.0, options.DefaultWallThickness * 3.5);
        var startAnchors = new List<WallSegment>();
        var endAnchors = new List<WallSegment>();
        foreach (var wall in walls)
        {
            if (wall.PageNumber != pageNumber
                || !IsSourceBackedExteriorShellClosureAnchor(wall, evidenceByWallId, options)
                || !TryResolveAxisInterval(wall.CenterLine, out var wallOrientation, out _, out _, out _)
                || wallOrientation == orientation)
            {
                continue;
            }

            if (DistanceFromPointToAxisSegment(line.Start, wall.CenterLine) <= tolerance)
            {
                startAnchors.Add(wall);
            }

            if (DistanceFromPointToAxisSegment(line.End, wall.CenterLine) <= tolerance)
            {
                endAnchors.Add(wall);
            }
        }

        if (startAnchors.Count == 0 || endAnchors.Count == 0)
        {
            return false;
        }

        var anchors = startAnchors
            .Concat(endAnchors)
            .DistinctBy(wall => wall.Id, StringComparer.Ordinal)
            .ToArray();
        var hasTrustedShellAnchor = anchors.Any(wall => IsTrustedSourceBackedExteriorShellClosureAnchor(wall, evidenceByWallId));
        if (!hasTrustedShellAnchor || anchors.Length < 2)
        {
            return false;
        }

        anchorWallIds = anchors
            .Select(wall => wall.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        evidence = new[]
        {
            $"wall evidence: source-backed shell closure start anchors {string.Join(",", startAnchors.Select(wall => wall.Id).Distinct(StringComparer.Ordinal))}",
            $"wall evidence: source-backed shell closure end anchors {string.Join(",", endAnchors.Select(wall => wall.Id).Distinct(StringComparer.Ordinal))}"
        };
        return true;
    }

    private static bool IsSourceBackedExteriorShellClosureAnchor(
        WallSegment wall,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> evidenceByWallId,
        ScannerOptions options)
    {
        if (wall.DrawingLength < Math.Max(42.0, options.MinWallLength * 1.75)
            || !TryResolveAxisInterval(wall.CenterLine, out _, out _, out _, out _)
            || !evidenceByWallId.TryGetValue(wall.Id, out var assessment)
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category is WallEvidenceCategory.DoorOrOpeningSymbol
                or WallEvidenceCategory.SurfacePatternDetail
                or WallEvidenceCategory.ObjectOrFixtureDetail)
        {
            return false;
        }

        if (wall.WallType == WallType.Exterior || IsTrustedSourceBackedExteriorShellClosureAnchor(wall, evidenceByWallId))
        {
            return true;
        }

        return assessment.Category is WallEvidenceCategory.StrongWallBody or WallEvidenceCategory.MediumWallBody
            && wall.PairEvidence is
            {
                Score: >= 0.84,
                OverlapRatio: >= 0.84,
                FaceSeparation: >= 1.5
            } pair
            && pair.FaceSeparation <= Math.Max(24.0, options.DefaultWallThickness * 5.0)
            && Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount) <= 180
            && pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount <= 280;
    }

    private static bool IsTrustedSourceBackedExteriorShellClosureAnchor(
        WallSegment wall,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> evidenceByWallId)
    {
        var evidence = wall.Evidence.Concat(
            evidenceByWallId.TryGetValue(wall.Id, out var assessment)
                ? assessment.Evidence
                    .Concat(assessment.ScoreBreakdown.PositiveEvidence)
                    .Concat(assessment.ScoreBreakdown.NegativeEvidence)
                : Array.Empty<string>());
        return wall.WallType == WallType.Exterior
            || wall.Id.Contains("wall-exterior-shell-inferred:", StringComparison.Ordinal)
            || wall.Id.Contains("wall-exterior-shell-source-backed:", StringComparison.Ordinal)
            || evidence.Any(item =>
                item.Contains("exterior shell", StringComparison.OrdinalIgnoreCase)
                || item.Contains("wall type exterior", StringComparison.OrdinalIgnoreCase)
                || item.Contains("source-backed exterior shell closure", StringComparison.OrdinalIgnoreCase));
    }

    private static double DistanceFromPointToAxisSegment(PlanPoint point, PlanLineSegment line)
    {
        var dx = line.End.X - line.Start.X;
        var dy = line.End.Y - line.Start.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= double.Epsilon)
        {
            return point.DistanceTo(line.Start);
        }

        var t = ((point.X - line.Start.X) * dx + (point.Y - line.Start.Y) * dy) / lengthSquared;
        t = Math.Clamp(t, 0, 1);
        var projection = new PlanPoint(line.Start.X + (dx * t), line.Start.Y + (dy * t));
        return point.DistanceTo(projection);
    }

    private static IEnumerable<RoomBoundaryInferenceEdge> ReliableIndoorRoomBoundaryEdgesForInference(
        IReadOnlyList<RoomRegion> rooms)
    {
        foreach (var room in rooms)
        {
            if (room.UseKind == RoomUseKind.Outdoor
                || room.Boundary.Count < 4
                || room.Confidence.Value < 0.58
                || !HasReliableRoomBoundaryEvidence(room))
            {
                continue;
            }

            for (var index = 0; index < room.Boundary.Count; index++)
            {
                var edge = new PlanLineSegment(room.Boundary[index], room.Boundary[(index + 1) % room.Boundary.Count]);
                if (edge.Length < 36.0
                    || !TryResolveAxisInterval(edge, out var orientation, out var coordinate, out var start, out var end))
                {
                    continue;
                }

                yield return new RoomBoundaryInferenceEdge(
                    room.Id,
                    room.PageNumber,
                    orientation,
                    coordinate,
                    start,
                    end);
            }
        }
    }

    private static bool HasReliableRoomBoundaryEvidence(RoomRegion room) =>
        RoomBoundaryReliability.HasReliableBoundaryEvidence(room);

    private static IEnumerable<RoomRegion> ReliableIndoorRoomsForExteriorShellInference(
        IReadOnlyList<RoomRegion> rooms) =>
        rooms.Where(room =>
            room.UseKind != RoomUseKind.Outdoor
            && room.Boundary.Count >= 4
            && room.Confidence.Value >= 0.58
            && !room.Bounds.IsEmpty
            && room.Bounds.Area >= 64.0
            && HasReliableRoomBoundaryEvidence(room));

    private static IEnumerable<ExteriorShellInferenceEdge> ReliableIndoorExteriorBoundaryEdgesForInference(
        IReadOnlyList<RoomRegion> rooms,
        IReadOnlyList<RoomRegion> indoorRooms,
        double sampleOffset,
        double minimumLength)
    {
        foreach (var room in indoorRooms)
        {
            for (var index = 0; index < room.Boundary.Count; index++)
            {
                var line = new PlanLineSegment(room.Boundary[index], room.Boundary[(index + 1) % room.Boundary.Count]);
                if (line.Length < minimumLength
                    || !TryResolveAxisInterval(line, out var orientation, out var coordinate, out var start, out var end))
                {
                    continue;
                }

                var midpoint = line.Midpoint;
                var firstSample = OffsetPoint(midpoint, orientation, -sampleOffset);
                var secondSample = OffsetPoint(midpoint, orientation, sampleOffset);
                var firstInsideThisRoom = PointInsideRoom(room, firstSample);
                var secondInsideThisRoom = PointInsideRoom(room, secondSample);
                if (firstInsideThisRoom == secondInsideThisRoom)
                {
                    continue;
                }

                var outsideSample = firstInsideThisRoom ? secondSample : firstSample;
                if (PointInsideAnyRoom(indoorRooms, outsideSample))
                {
                    continue;
                }

                var outsideDirection = firstInsideThisRoom ? 1 : -1;
                if (OutsideSideTouchesOutdoorRoom(rooms, room.PageNumber, line, orientation, outsideDirection, sampleOffset))
                {
                    continue;
                }

                var outsideSide = OutsideSideName(orientation, outsideDirection);
                yield return new ExteriorShellInferenceEdge(
                    room.Id,
                    room.PageNumber,
                    orientation,
                    coordinate,
                    start,
                    end,
                    AxisLine(orientation, coordinate, start, end),
                    outsideSide);
            }
        }
    }

    private static bool OutsideSideTouchesOutdoorRoom(
        IReadOnlyList<RoomRegion> rooms,
        int pageNumber,
        PlanLineSegment line,
        AxisOrientation orientation,
        int outsideDirection,
        double sampleOffset)
    {
        var outdoorRooms = rooms
            .Where(room => room.PageNumber == pageNumber
                && room.UseKind == RoomUseKind.Outdoor
                && !room.Bounds.IsEmpty)
            .ToArray();
        if (outdoorRooms.Length == 0)
        {
            return false;
        }

        foreach (var parameter in new[] { 0.2, 0.35, 0.5, 0.65, 0.8 })
        {
            var sample = OffsetPoint(line.PointAt(parameter), orientation, outsideDirection * sampleOffset);
            if (PointInsideAnyRoom(outdoorRooms, sample))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExteriorShellInferenceCovered(
        PlanLineSegment line,
        int pageNumber,
        IReadOnlyList<WallSegment> walls,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> evidenceByWallId,
        ScannerOptions options) =>
        walls
            .Where(wall => wall.PageNumber == pageNumber && wall.WallType == WallType.Exterior)
            .Where(wall => IsExistingExteriorWallTrustedForInferenceCoverage(wall, evidenceByWallId))
            .Any(wall =>
                AreNearParallel(line, wall.CenterLine)
                && Math.Max(
                    line.DistanceToPoint(wall.CenterLine.Midpoint),
                    wall.CenterLine.DistanceToPoint(line.Midpoint))
                    <= Math.Max(options.WallSnapTolerance * 4.0, options.DefaultWallThickness * 2.5)
                && AxisAlignedCoverageRatioOfFirst(line, wall.CenterLine) >= 0.58);

    private static bool HasExteriorShellInferenceLongSpanSupport(
        PlanLineSegment line,
        int pageNumber,
        IReadOnlyList<WallSegment> walls,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> evidenceByWallId,
        ScannerOptions options)
    {
        if (!TryResolveAxisInterval(line, out var orientation, out var coordinate, out var start, out var end))
        {
            return false;
        }

        var coordinateTolerance = Math.Max(options.WallSnapTolerance * 5.0, options.DefaultWallThickness * 3.0);
        var endpointTolerance = Math.Max(options.WallSnapTolerance * 5.0, options.DefaultWallThickness * 3.5);
        var collinearIntervals = new List<(double Start, double End)>();
        var hasEndpointAnchor = false;

        foreach (var wall in walls
            .Where(wall => wall.PageNumber == pageNumber && wall.WallType == WallType.Exterior)
            .Where(wall => IsExistingExteriorWallTrustedForInferenceCoverage(wall, evidenceByWallId)))
        {
            if (!TryResolveAxisInterval(wall.CenterLine, out var wallOrientation, out var wallCoordinate, out var wallStart, out var wallEnd))
            {
                continue;
            }

            if (wallOrientation == orientation)
            {
                if (Math.Abs(wallCoordinate - coordinate) > coordinateTolerance)
                {
                    continue;
                }

                var overlapStart = Math.Max(start, wallStart);
                var overlapEnd = Math.Min(end, wallEnd);
                if (overlapEnd > overlapStart)
                {
                    collinearIntervals.Add((overlapStart, overlapEnd));
                }

                continue;
            }

            hasEndpointAnchor =
                hasEndpointAnchor
                || EndpointTouchesAxisLine(wall.CenterLine.Start, orientation, coordinate, start, end, endpointTolerance)
                || EndpointTouchesAxisLine(wall.CenterLine.End, orientation, coordinate, start, end, endpointTolerance)
                || line.Start.DistanceTo(wall.CenterLine.Start) <= endpointTolerance
                || line.Start.DistanceTo(wall.CenterLine.End) <= endpointTolerance
                || line.End.DistanceTo(wall.CenterLine.Start) <= endpointTolerance
                || line.End.DistanceTo(wall.CenterLine.End) <= endpointTolerance;
        }

        if (collinearIntervals.Count == 0)
        {
            return false;
        }

        var coveredLength = MergedIntervalLength(collinearIntervals);
        var coverage = coveredLength / Math.Max(line.Length, 0.001);
        return coveredLength >= Math.Max(48.0, options.MinWallLength * 2.0)
            && coverage >= 0.16
            && hasEndpointAnchor;
    }

    private static bool TryFindExteriorShellInferenceSourceLineSupport(
        ScanContext context,
        int pageNumber,
        PlanLineSegment line,
        out IReadOnlyList<string> sourcePrimitiveIds,
        out double coverage)
    {
        sourcePrimitiveIds = Array.Empty<string>();
        coverage = 0;
        if (!TryResolveAxisInterval(line, out var orientation, out var coordinate, out var start, out var end))
        {
            return false;
        }

        var page = context.Document.Pages.FirstOrDefault(candidate => candidate.Number == pageNumber);
        if (page is null)
        {
            return false;
        }

        var coordinateTolerance = Math.Max(
            context.Options.WallSnapTolerance * 3.0,
            context.Options.DefaultWallThickness * 2.25);
        var minimumPrimitiveOverlap = Math.Max(24.0, context.Options.MinWallLength);
        var minimumCoveredLength = Math.Max(48.0, context.Options.MinWallLength * 2.0);
        var intervals = new List<(double Start, double End)>();
        var sourceIds = new List<string>();

        foreach (var primitiveLine in PrimitiveGeometry.EnumerateLines(page, context))
        {
            if (primitiveLine.Segment.Length < minimumPrimitiveOverlap
                || !TryResolveAxisInterval(primitiveLine.Segment, out var primitiveOrientation, out var primitiveCoordinate, out var primitiveStart, out var primitiveEnd)
                || primitiveOrientation != orientation
                || Math.Abs(primitiveCoordinate - coordinate) > coordinateTolerance)
            {
                continue;
            }

            var overlapStart = Math.Max(start, primitiveStart);
            var overlapEnd = Math.Min(end, primitiveEnd);
            if (overlapEnd - overlapStart < minimumPrimitiveOverlap)
            {
                continue;
            }

            intervals.Add((overlapStart, overlapEnd));
            sourceIds.Add(primitiveLine.PrimitiveId);
        }

        if (intervals.Count == 0)
        {
            return false;
        }

        var coveredLength = MergedIntervalLength(intervals);
        coverage = coveredLength / Math.Max(line.Length, 0.001);
        if (coveredLength < minimumCoveredLength || coverage < 0.68)
        {
            sourcePrimitiveIds = Array.Empty<string>();
            coverage = 0;
            return false;
        }

        sourcePrimitiveIds = sourceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        return sourcePrimitiveIds.Count > 0;
    }

    private static bool HasExteriorShellInferenceAnchor(
        PlanLineSegment line,
        int pageNumber,
        IReadOnlyList<WallSegment> walls,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> evidenceByWallId,
        ScannerOptions options)
    {
        if (!TryResolveAxisInterval(line, out var orientation, out var coordinate, out var start, out var end))
        {
            return false;
        }

        var coordinateTolerance = Math.Max(options.WallSnapTolerance * 5.0, options.DefaultWallThickness * 3.0);
        var endpointTolerance = Math.Max(options.WallSnapTolerance * 5.0, options.DefaultWallThickness * 3.5);
        var bridgeGapTolerance = Math.Max(options.MaxOpeningGap * 0.8, options.DefaultWallThickness * 10.0);
        foreach (var wall in walls
            .Where(wall => wall.PageNumber == pageNumber && wall.WallType == WallType.Exterior)
            .Where(wall => IsExistingExteriorWallTrustedForInferenceCoverage(wall, evidenceByWallId)))
        {
            if (!TryResolveAxisInterval(wall.CenterLine, out var wallOrientation, out var wallCoordinate, out var wallStart, out var wallEnd))
            {
                continue;
            }

            if (wallOrientation == orientation)
            {
                if (Math.Abs(wallCoordinate - coordinate) > coordinateTolerance)
                {
                    continue;
                }

                var beforeGap = start - wallEnd;
                var afterGap = wallStart - end;
                if (beforeGap >= -coordinateTolerance && beforeGap <= bridgeGapTolerance
                    || afterGap >= -coordinateTolerance && afterGap <= bridgeGapTolerance)
                {
                    return true;
                }

                continue;
            }

            if (EndpointTouchesAxisLine(wall.CenterLine.Start, orientation, coordinate, start, end, endpointTolerance)
                || EndpointTouchesAxisLine(wall.CenterLine.End, orientation, coordinate, start, end, endpointTolerance)
                || line.Start.DistanceTo(wall.CenterLine.Start) <= endpointTolerance
                || line.Start.DistanceTo(wall.CenterLine.End) <= endpointTolerance
                || line.End.DistanceTo(wall.CenterLine.Start) <= endpointTolerance
                || line.End.DistanceTo(wall.CenterLine.End) <= endpointTolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static bool EndpointTouchesAxisLine(
        PlanPoint endpoint,
        AxisOrientation orientation,
        double coordinate,
        double start,
        double end,
        double tolerance)
    {
        var endpointCoordinate = orientation == AxisOrientation.Horizontal ? endpoint.Y : endpoint.X;
        var endpointAlong = orientation == AxisOrientation.Horizontal ? endpoint.X : endpoint.Y;
        return Math.Abs(endpointCoordinate - coordinate) <= tolerance
            && endpointAlong >= Math.Min(start, end) - tolerance
            && endpointAlong <= Math.Max(start, end) + tolerance;
    }

    private static bool IsExistingExteriorWallTrustedForInferenceCoverage(
        WallSegment wall,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> evidenceByWallId)
    {
        if (wall.Id.Contains("wall-exterior-shell-inferred:", StringComparison.Ordinal)
            || wall.Evidence.Any(item => item.Contains("inferred exterior shell wall", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!evidenceByWallId.TryGetValue(wall.Id, out var assessment)
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject)
        {
            return false;
        }

        return !wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Any(item =>
                item.Contains("outdoor covered-area", StringComparison.OrdinalIgnoreCase)
                || item.Contains("covered-area boundary", StringComparison.OrdinalIgnoreCase)
                || item.Contains("covered entry", StringComparison.OrdinalIgnoreCase)
                || item.Contains("covered-entry", StringComparison.OrdinalIgnoreCase)
                || item.Contains("overbygd", StringComparison.OrdinalIgnoreCase)
                || item.Contains("terrace", StringComparison.OrdinalIgnoreCase)
                || item.Contains("canopy", StringComparison.OrdinalIgnoreCase)
                || item.Contains("railing", StringComparison.OrdinalIgnoreCase)
                || item.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
                || item.Contains("door/opening", StringComparison.OrdinalIgnoreCase)
                || item.Contains("non-wall", StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<int> BuildExteriorShellInferenceEligiblePages(
        IReadOnlyList<WallSegment> walls,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> evidenceByWallId,
        ScannerOptions options)
    {
        var result = new HashSet<int>();
        foreach (var group in walls
            .Where(wall => wall.WallType == WallType.Exterior)
            .Where(wall => IsExistingExteriorWallTrustedForInferenceCoverage(wall, evidenceByWallId))
            .GroupBy(wall => wall.PageNumber))
        {
            var trustedWalls = group.ToArray();
            if (trustedWalls.Length < 3)
            {
                continue;
            }

            var hasHorizontal = trustedWalls.Any(wall =>
                TryResolveAxisInterval(wall.CenterLine, out var orientation, out _, out _, out _)
                && orientation == AxisOrientation.Horizontal);
            var hasVertical = trustedWalls.Any(wall =>
                TryResolveAxisInterval(wall.CenterLine, out var orientation, out _, out _, out _)
                && orientation == AxisOrientation.Vertical);
            var trustedLength = trustedWalls.Sum(wall => wall.DrawingLength);
            if (hasHorizontal
                && hasVertical
                && trustedLength >= Math.Max(240.0, options.MinWallLength * 10.0))
            {
                result.Add(group.Key);
            }
        }

        return result;
    }

    private static PlanPoint OffsetPoint(PlanPoint point, AxisOrientation orientation, double offset) =>
        orientation == AxisOrientation.Horizontal
            ? new PlanPoint(point.X, point.Y + offset)
            : new PlanPoint(point.X + offset, point.Y);

    private static string OutsideSideName(AxisOrientation orientation, double direction) =>
        orientation == AxisOrientation.Horizontal
            ? direction < 0 ? "top" : "bottom"
            : direction < 0 ? "left" : "right";

    private static bool PointInsideAnyRoom(IEnumerable<RoomRegion> rooms, PlanPoint point) =>
        rooms.Any(room => PointInsideRoom(room, point));

    private static bool PointInsideRoom(RoomRegion room, PlanPoint point)
    {
        if (!room.Bounds.Contains(point, 0.5))
        {
            return false;
        }

        if (room.Boundary.Count < 3)
        {
            return true;
        }

        var inside = false;
        var previous = room.Boundary[^1];
        foreach (var current in room.Boundary)
        {
            var denominator = previous.Y - current.Y;
            var crosses =
                (current.Y > point.Y) != (previous.Y > point.Y)
                && Math.Abs(denominator) > 0.000001
                && point.X < (previous.X - current.X) * (point.Y - current.Y) / denominator + current.X;
            if (crosses)
            {
                inside = !inside;
            }

            previous = current;
        }

        return inside;
    }

    private static string ExteriorShellInferenceKey(
        int pageNumber,
        AxisOrientation orientation,
        double coordinate,
        double start,
        double end) =>
        string.Join(
            ":",
            pageNumber.ToString(CultureInfo.InvariantCulture),
            "ExteriorShell",
            orientation.ToString(),
            Math.Round(coordinate / 4.0).ToString(CultureInfo.InvariantCulture),
            Math.Round(start / 8.0).ToString(CultureInfo.InvariantCulture),
            Math.Round(end / 8.0).ToString(CultureInfo.InvariantCulture));

    private static bool AreNearParallel(PlanLineSegment first, PlanLineSegment second)
    {
        var firstVector = first.Vector.Normalize();
        var secondVector = second.Vector.Normalize();
        if (firstVector.Length <= double.Epsilon || secondVector.Length <= double.Epsilon)
        {
            return false;
        }

        var cross = Math.Abs((firstVector.X * secondVector.Y) - (firstVector.Y * secondVector.X));
        return cross <= 0.035;
    }

    private static double AxisAlignedOverlapRatio(PlanLineSegment first, PlanLineSegment second)
    {
        if (!TryResolveAxisInterval(first, out var firstOrientation, out _, out var firstStart, out var firstEnd)
            || !TryResolveAxisInterval(second, out var secondOrientation, out _, out var secondStart, out var secondEnd)
            || firstOrientation != secondOrientation)
        {
            return 0;
        }

        var overlap = Math.Min(firstEnd, secondEnd) - Math.Max(firstStart, secondStart);
        if (overlap <= 0)
        {
            return 0;
        }

        return overlap / Math.Max(1.0, Math.Min(first.Length, second.Length));
    }

    private static double AxisAlignedCoverageRatioOfFirst(PlanLineSegment first, PlanLineSegment second)
    {
        if (!TryResolveAxisInterval(first, out var firstOrientation, out _, out var firstStart, out var firstEnd)
            || !TryResolveAxisInterval(second, out var secondOrientation, out _, out var secondStart, out var secondEnd)
            || firstOrientation != secondOrientation)
        {
            return 0;
        }

        var overlap = Math.Min(firstEnd, secondEnd) - Math.Max(firstStart, secondStart);
        if (overlap <= 0)
        {
            return 0;
        }

        return overlap / Math.Max(1.0, first.Length);
    }

    private static double MergedIntervalLength(IEnumerable<(double Start, double End)> intervals)
    {
        var ordered = intervals
            .Select(item => (Start: Math.Min(item.Start, item.End), End: Math.Max(item.Start, item.End)))
            .Where(item => item.End > item.Start)
            .OrderBy(item => item.Start)
            .ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var total = 0.0;
        var start = ordered[0].Start;
        var end = ordered[0].End;
        for (var index = 1; index < ordered.Length; index++)
        {
            var current = ordered[index];
            if (current.Start <= end)
            {
                end = Math.Max(end, current.End);
                continue;
            }

            total += end - start;
            start = current.Start;
            end = current.End;
        }

        total += end - start;
        return total;
    }

    private static bool IsSharedBoundaryInferenceCovered(
        PlanLineSegment line,
        int pageNumber,
        IReadOnlyList<WallSegment> walls,
        ScannerOptions options) =>
        walls
            .Where(wall => wall.PageNumber == pageNumber)
            .Any(wall =>
                AreNearParallel(line, wall.CenterLine)
                && Math.Max(
                    line.DistanceToPoint(wall.CenterLine.Midpoint),
                    wall.CenterLine.DistanceToPoint(line.Midpoint))
                    <= Math.Max(options.WallSnapTolerance * 3.0, options.DefaultWallThickness * 2.0)
                && AxisAlignedOverlapRatio(line, wall.CenterLine) >= 0.45);

    private static bool RoomBoundaryRepairSupportTouchesLine(
        RoomBoundaryRepairSupportResult support,
        PlanLineSegment line,
        int pageNumber,
        ScannerOptions options) =>
        support.SupportByWallId.Values.Any(item =>
            item.PageNumber == pageNumber
            && AreNearParallel(line, item.RoomBoundaryEdge)
            && Math.Max(
                line.DistanceToPoint(item.RoomBoundaryEdge.Midpoint),
                item.RoomBoundaryEdge.DistanceToPoint(line.Midpoint))
                <= Math.Max(options.WallSnapTolerance * 3.0, options.DefaultWallThickness * 2.0)
            && AxisAlignedOverlapRatio(line, item.RoomBoundaryEdge) >= 0.45);

    private static PlanLineSegment AxisLine(
        AxisOrientation orientation,
        double coordinate,
        double start,
        double end) =>
        orientation == AxisOrientation.Horizontal
            ? new PlanLineSegment(new PlanPoint(start, coordinate), new PlanPoint(end, coordinate))
            : new PlanLineSegment(new PlanPoint(coordinate, start), new PlanPoint(coordinate, end));

    private static string SharedBoundaryInferenceKey(
        int pageNumber,
        AxisOrientation orientation,
        double coordinate,
        double start,
        double end) =>
        string.Join(
            ":",
            pageNumber.ToString(CultureInfo.InvariantCulture),
            orientation.ToString(),
            Math.Round(coordinate / 4.0).ToString(CultureInfo.InvariantCulture),
            Math.Round(start / 8.0).ToString(CultureInfo.InvariantCulture),
            Math.Round(end / 8.0).ToString(CultureInfo.InvariantCulture));

    private static bool TryRecoverRejectedRoomBoundaryWallCandidate(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        RoomBoundaryRepairSupport support,
        RoomSideEvidence sideEvidence,
        ScannerOptions options,
        out WallSegment recoveredWall,
        out WallEvidenceWallAssessment recoveredAssessment)
    {
        recoveredWall = wall;
        recoveredAssessment = assessment;

        if ((!assessment.RejectedAsNoise && assessment.Decision != WallEvidenceDecision.Reject)
            || wall.WallType == WallType.Exterior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is not { } pair
            || pair.Score < 0.82
            || pair.OverlapRatio < 0.90
            || support.WallCoverageRatio < 0.68
            || support.EdgeCoverageRatio < 0.20
            || support.OverlapLength < Math.Max(42.0, options.MinWallLength * 1.75)
            || sideEvidence.HasOutdoorRoomSide
            || !IsRecoverableRejectedRoomBoundaryCategory(wall, assessment)
            || HasRoomBoundaryRepairPromotionBlocker(
                wall,
                assessment,
                allowGraphObjectLikeReclassification: IsGraphObjectLikeRoomBoundaryFalsePositive(wall, assessment)))
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();
        if (IsDimensionLikeWeakLayerEvidence(evidence)
            && (!sideEvidence.HasRoomsOnBothSides
                || pair.Score < 0.90
                || support.WallCoverageRatio < 0.85))
        {
            return false;
        }

        var recoveryEvidence = support.Evidence
            .Concat(new[]
            {
                "wall evidence: rejected room-boundary candidate restored after unsupported indoor room edge scan",
                $"wall evidence: rejected room-boundary recovery pair score {pair.Score.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, overlap ratio {pair.OverlapRatio.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, side room hits {(sideEvidence.PositiveRoomHits + sideEvidence.NegativeRoomHits).ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            })
            .ToArray();
        var wallTypeEvidence = wall.WallType == WallType.Unknown
            ? new[] { "wall type refined interior: rejected room-boundary recovery matched an unsupported indoor room edge" }
            : Array.Empty<string>();
        recoveredWall = wall with
        {
            WallType = wall.WallType == WallType.Unknown ? WallType.Interior : wall.WallType,
            Evidence = AppendEvidence(wall.Evidence, recoveryEvidence.Concat(wallTypeEvidence))
        };
        recoveredAssessment = assessment with
        {
            Category = WallEvidenceCategory.MediumWallBody,
            Confidence = new Confidence(Math.Max(assessment.Confidence.Value, Math.Min(0.92, pair.Score))),
            PlacementReady = true,
            RequiresReview = false,
            RejectedAsNoise = false,
            Decision = WallEvidenceDecision.Accept,
            Evidence = AppendEvidence(assessment.Evidence, recoveryEvidence.Concat(wallTypeEvidence))
        };
        return true;
    }

    private static bool TryRecoverRejectedExteriorShellWallCandidate(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        ExteriorShellRepairSupport support,
        out WallSegment recoveredWall,
        out WallEvidenceWallAssessment recoveredAssessment)
    {
        recoveredWall = wall;
        recoveredAssessment = assessment;

        if ((!assessment.RejectedAsNoise && assessment.Decision != WallEvidenceDecision.Reject)
            || (!IsSupportedExteriorShellStrokeRecovery(wall, assessment, support)
                && (wall.DetectionKind != WallDetectionKind.ParallelLinePair
                    || wall.PairEvidence is not { } pair
                    || pair.Score < (support.SupportKind == "global-envelope-fragment-chain" ? 0.66 : 0.70)
                    || pair.OverlapRatio < (support.SupportKind == "global-envelope-fragment-chain" ? 0.70 : 0.84)
                    || wall.FragmentEvidence?.RequiresGeometryReview == true))
            || !IsRecoverableRejectedExteriorShellCategory(wall, assessment)
            || HasExteriorShellRecoveryBlocker(
                wall,
                assessment,
                allowGraphObjectLikeReclassification: IsGraphObjectLikeRoomBoundaryFalsePositive(wall, assessment),
                allowDimensionLikeStructuralShell: support.SupportKind == "global-envelope-fragment-chain"))
        {
            return false;
        }

        var recoveryEvidence = support.Evidence
            .Concat(new[]
            {
                "wall evidence: rejected exterior-shell candidate restored after global shell continuity scan",
                wall.PairEvidence is { } recoveredPair
                    ? $"wall evidence: rejected exterior-shell recovery pair score {recoveredPair.Score.ToString("0.###", CultureInfo.InvariantCulture)}, overlap ratio {recoveredPair.OverlapRatio.ToString("0.###", CultureInfo.InvariantCulture)}, support kind {support.SupportKind}"
                    : $"wall evidence: rejected exterior-shell recovery structural stroke support score {support.SupportScore.ToString("0.###", CultureInfo.InvariantCulture)}, length {wall.DrawingLength.ToString("0.###", CultureInfo.InvariantCulture)}, support kind {support.SupportKind}"
            })
            .ToArray();
        var recoveredConfidenceBoost = wall.PairEvidence is { } recoveredConfidencePair
            ? recoveredConfidencePair.Score + 0.04
            : support.SupportScore;
        recoveredWall = wall with
        {
            WallType = WallType.Exterior,
            Evidence = AppendEvidence(wall.Evidence, recoveryEvidence)
        };
        recoveredAssessment = assessment with
        {
            Category = WallEvidenceCategory.MediumWallBody,
            Confidence = new Confidence(Math.Max(assessment.Confidence.Value, Math.Min(0.92, recoveredConfidenceBoost))),
            PlacementReady = true,
            RequiresReview = false,
            RejectedAsNoise = false,
            Decision = WallEvidenceDecision.Accept,
            Evidence = AppendEvidence(assessment.Evidence, recoveryEvidence)
        };
        return true;
    }

    private static bool IsRecoverableRejectedRoomBoundaryCategory(
        WallSegment wall,
        WallEvidenceWallAssessment assessment)
    {
        if (assessment.Category is WallEvidenceCategory.StrongWallBody
            or WallEvidenceCategory.MediumWallBody
            or WallEvidenceCategory.RecoveredWallBody)
        {
            return true;
        }

        return assessment.Category == WallEvidenceCategory.ObjectOrFixtureDetail
            && IsGraphObjectLikeRoomBoundaryFalsePositive(wall, assessment);
    }

    private static bool IsRecoverableRejectedExteriorShellCategory(
        WallSegment wall,
        WallEvidenceWallAssessment assessment)
    {
        if (assessment.Category is WallEvidenceCategory.StrongWallBody
            or WallEvidenceCategory.MediumWallBody
            or WallEvidenceCategory.RecoveredWallBody)
        {
            return true;
        }

        return assessment.Category == WallEvidenceCategory.ObjectOrFixtureDetail
            && IsGraphObjectLikeRoomBoundaryFalsePositive(wall, assessment);
    }

    private static bool IsPromotableExteriorShellStroke(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        ExteriorShellRepairSupport support) =>
        IsSupportedExteriorShellStrokeRecovery(wall, assessment, support)
        && !assessment.RejectedAsNoise
        && assessment.Decision != WallEvidenceDecision.Reject;

    private static bool IsSupportedExteriorShellStrokeRecovery(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        ExteriorShellRepairSupport support)
    {
        if (support.SupportKind != "global-envelope-fragment-chain"
            || wall.DetectionKind is not (WallDetectionKind.FragmentMerged or WallDetectionKind.SingleLine)
            || wall.PairEvidence is not null
            || wall.DrawingLength < 72.0
            || support.SupportScore < 0.88
            || assessment.Category is not (WallEvidenceCategory.StrongWallBody
                or WallEvidenceCategory.MediumWallBody
                or WallEvidenceCategory.RecoveredWallBody))
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(support.Evidence)
            .ToArray();
        var hasExteriorEvidence = wall.WallType == WallType.Exterior
            || evidence.Any(item =>
                item.Contains("wall type exterior", StringComparison.OrdinalIgnoreCase)
                || item.Contains("near detected floorplan/wall envelope", StringComparison.OrdinalIgnoreCase)
                || item.Contains("local outer boundary", StringComparison.OrdinalIgnoreCase)
                || item.Contains("same envelope edge", StringComparison.OrdinalIgnoreCase));
        if (!hasExteriorEvidence)
        {
            return false;
        }

        return !evidence.Any(item =>
            item.Contains("outdoor covered-area", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered-area boundary", StringComparison.OrdinalIgnoreCase)
            || item.Contains("unpaired outdoor", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered entry", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered-entry", StringComparison.OrdinalIgnoreCase)
            || item.Contains("overbygd", StringComparison.OrdinalIgnoreCase)
            || item.Contains("terrace", StringComparison.OrdinalIgnoreCase)
            || item.Contains("canopy", StringComparison.OrdinalIgnoreCase)
            || item.Contains("railing", StringComparison.OrdinalIgnoreCase)
            || item.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
            || item.Contains("repeated short detail", StringComparison.OrdinalIgnoreCase)
            || item.Contains("fixture detail", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door/opening", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door swing", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door leaf", StringComparison.OrdinalIgnoreCase)
            || item.Contains("door arc", StringComparison.OrdinalIgnoreCase)
            || item.Contains("witness/extension", StringComparison.OrdinalIgnoreCase)
            || item.Contains("stair", StringComparison.OrdinalIgnoreCase)
            || item.Contains("non-wall", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasExteriorShellRecoveryBlocker(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        bool allowGraphObjectLikeReclassification,
        bool allowDimensionLikeStructuralShell) =>
        wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Any(item =>
            {
                if (allowGraphObjectLikeReclassification
                    && (IsGraphObjectLikeReclassificationEvidence(item)
                        || item.Contains("explicit non-wall evidence: ObjectOrFixtureDetail", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                if (allowDimensionLikeStructuralShell
                    && (item.Contains("dimension-like", StringComparison.OrdinalIgnoreCase)
                        || item.Contains("dimension/annotation", StringComparison.OrdinalIgnoreCase)
                        || item.Contains("classified Dimension", StringComparison.OrdinalIgnoreCase)
                        || item.Contains("layer evidence: contains dimension", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                return item.Contains("outdoor covered-area", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("covered-area boundary", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("unpaired outdoor", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("covered entry", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("covered-entry", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("overbygd", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("terrace", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("canopy", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("railing", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("surface/detail pattern", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("repeated short detail", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("fixture detail", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("door/opening", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("door swing", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("door leaf", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("door arc", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("dimension-like", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("dimension/annotation", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("witness/extension", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("stair", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("non-wall", StringComparison.OrdinalIgnoreCase);
            });

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
        int unsupportedRoomBoundaryEdgeCount,
        int roomBoundaryRepairCandidateWallCount,
        int roomBoundaryRepairPlacementPromoted,
        int roomBoundaryRepairInteriorRefined,
        int roomBoundaryRepairRejectedRecovered,
        int exteriorShellRepairCandidateWallCount,
        int exteriorShellRepairPlacementPromoted,
        int exteriorShellRepairRejectedRecovered,
        int sharedRoomBoundaryGapInferred,
        int sourceBackedExteriorShellClosureInferred,
        int exteriorShellGapInferred,
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
                ["unsupportedRoomBoundaryEdgeCount"] = unsupportedRoomBoundaryEdgeCount.ToString(),
                ["roomBoundaryRepairCandidateWallCount"] = roomBoundaryRepairCandidateWallCount.ToString(),
                ["roomBoundaryRepairPlacementPromotedWallCount"] = roomBoundaryRepairPlacementPromoted.ToString(),
                ["roomBoundaryRepairInteriorRefinedWallCount"] = roomBoundaryRepairInteriorRefined.ToString(),
                ["roomBoundaryRepairRejectedRecoveredWallCount"] = roomBoundaryRepairRejectedRecovered.ToString(),
                ["exteriorShellRepairCandidateWallCount"] = exteriorShellRepairCandidateWallCount.ToString(),
                ["exteriorShellRepairPlacementPromotedWallCount"] = exteriorShellRepairPlacementPromoted.ToString(),
                ["exteriorShellRepairRejectedRecoveredWallCount"] = exteriorShellRepairRejectedRecovered.ToString(),
                ["sharedRoomBoundaryGapInferredWallCount"] = sharedRoomBoundaryGapInferred.ToString(),
                ["sourceBackedExteriorShellClosureInferredWallCount"] = sourceBackedExteriorShellClosureInferred.ToString(),
                ["exteriorShellGapInferredWallCount"] = exteriorShellGapInferred.ToString(),
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

    private readonly record struct RoomBoundaryInferenceEdge(
        string RoomId,
        int PageNumber,
        AxisOrientation Orientation,
        double Coordinate,
        double Start,
        double End)
    {
        public double Length => End - Start;
    }

    private readonly record struct ExteriorShellInferenceEdge(
        string RoomId,
        int PageNumber,
        AxisOrientation Orientation,
        double Coordinate,
        double Start,
        double End,
        PlanLineSegment Line,
        string OutsideSide)
    {
        public double Length => End - Start;
    }

    private sealed record SourceBackedExteriorShellClosureCandidate(
        AxisOrientation Orientation,
        double Coordinate,
        double Start,
        double End,
        PlanLineSegment Line,
        IReadOnlyList<string> SourcePrimitiveIds);

    private enum AxisOrientation
    {
        Unknown,
        Horizontal,
        Vertical
    }

    private sealed record WallTypeRefinement(WallType WallType, string Evidence);
}
