namespace OpenPlanTrace;

public static class PlanRoutingLayerBuilder
{
    private const double ShortUnreferencedRoutingBarrierLengthMeters = 1.25;
    private const double ShortUnreferencedRoutingBarrierDrawingLength = 36.0;
    private const double RoutingNodeMergeTolerance = 0.75;
    private const int DenseMinorRoutingPatternJunctionThreshold = 4;

    public static PlanRoutingLayer FromScanResult(PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var wallComponentLookup = BuildWallComponentLookup(result.WallGraph.Components);
        var protectedRoutingWallIds = BuildProtectedRoutingWallIds(result);
        var structuralComponentPages = BuildStructuralComponentPages(result.WallGraph.Components);
        var roomSolvedPages = BuildRoomSolvedPages(result.Rooms);
        var denseMinorRoutingDetailPatterns = DetectDenseMinorRoutingDetailPatterns(result);
        var denseMinorRoutingDetailWallIds = denseMinorRoutingDetailPatterns
            .SelectMany(pattern => pattern.WallIds)
            .ToHashSet(StringComparer.Ordinal);
        var fragmentReviewWallIds = result.Walls
            .Where(wall => wall.FragmentEvidence?.RequiresGeometryReview == true)
            .Select(wall => wall.Id)
            .ToHashSet(StringComparer.Ordinal);
        var rejectedEvidenceWallIds = BuildRejectedWallEvidenceIds(result);
        var reviewRequiredEvidenceWallIds = BuildReviewRequiredWallEvidenceIds(result);
        var allBarriers = BuildRoutingBarriers(result, wallComponentLookup);
        var suppressedFragmentReviewBarrierCount = allBarriers.Count(barrier =>
            fragmentReviewWallIds.Contains(barrier.SourceId));
        var suppressedRejectedEvidenceBarrierCount = allBarriers.Count(barrier =>
            rejectedEvidenceWallIds.Contains(barrier.SourceId));
        var suppressedReviewEvidenceBarrierCount = allBarriers.Count(barrier =>
            !fragmentReviewWallIds.Contains(barrier.SourceId)
            && !rejectedEvidenceWallIds.Contains(barrier.SourceId)
            && IsUnprotectedReviewEvidenceRoutingBarrier(
                barrier,
                reviewRequiredEvidenceWallIds,
                protectedRoutingWallIds));
        var suppressedIsolatedBarrierCount = allBarriers.Count(barrier =>
            IsUnusedIsolatedRoutingBarrier(barrier, protectedRoutingWallIds, structuralComponentPages));
        var suppressedShortUnreferencedBarrierCount = allBarriers.Count(barrier =>
            IsUnusedShortStructuralRoutingBarrier(barrier, protectedRoutingWallIds, roomSolvedPages));
        var suppressedDenseMinorDetailBarrierCount = allBarriers.Count(barrier =>
            IsDenseMinorRoutingDetailBarrier(barrier, denseMinorRoutingDetailWallIds));
        var barriers = allBarriers
            .Where(barrier => !fragmentReviewWallIds.Contains(barrier.SourceId))
            .Where(barrier => !rejectedEvidenceWallIds.Contains(barrier.SourceId))
            .Where(barrier => !IsUnprotectedReviewEvidenceRoutingBarrier(
                barrier,
                reviewRequiredEvidenceWallIds,
                protectedRoutingWallIds))
            .Where(barrier => !barrier.ExcludedFromStructuralTopology)
            .Where(barrier => !IsUnusedIsolatedRoutingBarrier(barrier, protectedRoutingWallIds, structuralComponentPages))
            .Where(barrier => !IsUnusedShortStructuralRoutingBarrier(barrier, protectedRoutingWallIds, roomSolvedPages))
            .Where(barrier => !IsDenseMinorRoutingDetailBarrier(barrier, denseMinorRoutingDetailWallIds))
            .ToArray();

        var passages = result.Openings
            .Select(CreatePassage)
            .ToArray();

        var suppressedObjects = CreateSuppressedObjects(result);
        var suppressedObjectIds = result.ObjectAggregates
            .Where(aggregate => aggregate.SuppressChildObjectsForRouting)
            .SelectMany(aggregate => aggregate.ChildObjectIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var suppressedLookup = suppressedObjectIds.ToHashSet(StringComparer.Ordinal);

        var obstacles = new List<RoutingObstacle>();
        foreach (var aggregate in result.ObjectAggregates)
        {
            var obstacleKind = MapObstacleKind(aggregate.RoutingInfluence);
            if (obstacleKind is not RoutingObstacleKind.Unknown)
            {
                obstacles.Add(CreateAggregateObstacle(aggregate, obstacleKind));
            }
        }

        var ignoredObjectIds = new SortedSet<string>(StringComparer.Ordinal);
        var ignoredObjects = new List<RoutingIgnoredObject>();
        var suppressedObjectLookup = suppressedObjects.ToDictionary(item => item.ObjectCandidateId, StringComparer.Ordinal);
        foreach (var candidate in result.ObjectCandidates)
        {
            if (suppressedLookup.Contains(candidate.Id))
            {
                ignoredObjectIds.Add(candidate.Id);
                suppressedObjectLookup.TryGetValue(candidate.Id, out var suppressedObject);
                ignoredObjects.Add(CreateIgnoredObject(candidate, RoutingIgnoredObjectReason.SuppressedByAggregate, suppressedObject));
                continue;
            }

            var influence = InferCandidateRoutingInfluence(candidate);
            var obstacleKind = MapObstacleKind(influence);
            if (obstacleKind is RoutingObstacleKind.Unknown)
            {
                ignoredObjectIds.Add(candidate.Id);
                ignoredObjects.Add(CreateIgnoredObject(candidate, IgnoredReasonFor(candidate, influence), suppressedObject: null));
                continue;
            }

            obstacles.Add(CreateCandidateObstacle(candidate, influence, obstacleKind));
        }

        var roomUseHints = result.Rooms
            .Where(room => room.UseKind is not RoomUseKind.Unknown)
            .Select(CreateRoomUseHint)
            .Concat(result.ObjectAggregates
                .Where(aggregate => aggregate.RoomUseEvidence is not RoomUseKind.Unknown)
                .Select(CreateAggregateRoomUseHint))
            .Concat(result.ObjectCandidates
                .Where(candidate => !suppressedLookup.Contains(candidate.Id))
                .Where(candidate => InferCandidateRoomUseHint(candidate) is not RoomUseKind.Unknown)
                .Select(candidate => CreateCandidateRoomUseHint(candidate, InferCandidateRoomUseHint(candidate))))
            .ToArray();

        var evidence = new[]
        {
            $"routing barriers from structural wall evidence: {barriers.Length}",
            $"routing barriers are split at wall graph junctions when node geometry is available",
            $"unused isolated wall fragments suppressed as routing barriers: {suppressedIsolatedBarrierCount}",
            $"short unreferenced wall fragments suppressed as routing barriers: {suppressedShortUnreferencedBarrierCount}",
            $"dense minor-detail routing barriers suppressed: {suppressedDenseMinorDetailBarrierCount}",
            $"fragment-review wall barriers suppressed: {suppressedFragmentReviewBarrierCount}",
            $"wall-evidence rejected barriers suppressed: {suppressedRejectedEvidenceBarrierCount}",
            $"wall-evidence review barriers suppressed: {suppressedReviewEvidenceBarrierCount}",
            $"routing passages from opening evidence: {passages.Length}",
            $"routing obstacles after aggregate suppression: {obstacles.Count}",
            $"suppressed child object candidates: {suppressedObjectIds.Length}",
            $"suppression records with downstream actions: {suppressedObjects.Count}",
            $"ignored object routing records: {ignoredObjects.Count}",
            $"room-use hints: {roomUseHints.Length}"
        };

        return new PlanRoutingLayer(
            barriers,
            passages,
            obstacles.OrderBy(obstacle => obstacle.PageNumber).ThenBy(obstacle => obstacle.Id, StringComparer.Ordinal).ToArray(),
            roomUseHints.OrderBy(hint => hint.PageNumber).ThenBy(hint => hint.Id, StringComparer.Ordinal).ToArray(),
            suppressedObjects,
            ignoredObjects
                .OrderBy(item => item.PageNumber)
                .ThenBy(item => item.ObjectCandidateId, StringComparer.Ordinal)
                .ToArray(),
            suppressedObjectIds,
            ignoredObjectIds.ToArray(),
            evidence);
    }

    private static IReadOnlySet<string> BuildRejectedWallEvidenceIds(PlanScanResult result) =>
        result.WallEvidenceMap.WallAssessments
            .Where(assessment => assessment.RejectedAsNoise || assessment.Decision == WallEvidenceDecision.Reject)
            .Select(assessment => assessment.WallId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> BuildReviewRequiredWallEvidenceIds(PlanScanResult result) =>
        result.WallEvidenceMap.WallAssessments
            .Where(assessment => !assessment.RejectedAsNoise)
            .Where(assessment => assessment.Decision != WallEvidenceDecision.Reject)
            .Where(assessment => assessment.RequiresReview || assessment.Decision == WallEvidenceDecision.Review)
            .Select(assessment => assessment.WallId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

    private static RoutingBarrier CreateBarrier(
        WallSegment wall,
        IReadOnlyDictionary<string, WallGraphComponent> wallComponentLookup)
    {
        wallComponentLookup.TryGetValue(wall.Id, out var component);
        var evidence = new List<string>(wall.Evidence);
        if (component is not null)
        {
            evidence.Add($"wall graph component {component.Id} classified as {component.Kind}");
            if (component.ExcludedFromStructuralTopology)
            {
                evidence.Add("component is excluded from structural topology and routing barriers");
            }
        }

        return new RoutingBarrier(
            $"routing-barrier:{wall.Id}",
            wall.PageNumber,
            wall.Id,
            RoutingSourceKind.Wall,
            wall.CenterLine,
            wall.Bounds,
            wall.Thickness,
            wall.DrawingLength,
            wall.LengthMeters,
            wall.ThicknessMillimeters,
            wall.MeasurementScaleGroupId,
            component?.Id,
            component?.Kind,
            component?.ExcludedFromStructuralTopology ?? false,
            wall.Confidence,
            wall.SourcePrimitiveIds,
            evidence);
    }

    private static RoutingPassage CreatePassage(OpeningCandidate opening)
    {
        var evidence = new List<string>(opening.Evidence);
        var placementReady = ScanReviewQueueSummary.OpeningPlacementIsCoordinateReady(opening);
        var reviewReasons = ScanReviewQueueSummary.OpeningReviewReasons(opening).ToArray();
        if (!placementReady)
        {
            evidence.Add("opening placement is not coordinate-ready; routing passage requires review before exact placement use");
        }

        return new(
            $"routing-passage:{opening.Id}",
            opening.PageNumber,
            opening.Id,
            RoutingSourceKind.Opening,
            opening.Type,
            opening.Operation,
            opening.Orientation,
            opening.CenterLine,
            opening.Bounds,
            opening.DrawingWidth,
            opening.WidthMillimeters,
            opening.MeasurementScaleGroupId,
            opening.HostWallIds,
            opening.ConnectedRoomIds,
            opening.ConnectedRoomLabels,
            opening.ConnectedRoomLinks,
            opening.RoomAdjacencyIds,
            opening.Placement,
            placementReady,
            reviewReasons.Length > 0,
            reviewReasons,
            opening.Confidence,
            opening.SourcePrimitiveIds,
            evidence);
    }

    private static RoutingObstacle CreateAggregateObstacle(
        ObjectAggregate aggregate,
        RoutingObstacleKind obstacleKind) =>
        new(
            $"routing-obstacle:{aggregate.Id}",
            aggregate.PageNumber,
            aggregate.Id,
            RoutingSourceKind.ObjectAggregate,
            obstacleKind,
            aggregate.RoutingInfluence,
            aggregate.StructuralInfluence,
            aggregate.Category,
            aggregate.Kind,
            aggregate.Bounds,
            aggregate.Label,
            aggregate.RoomId,
            aggregate.RoomLabel,
            aggregate.SuppressChildObjectsForRouting,
            aggregate.ChildObjectIds,
            aggregate.Confidence,
            aggregate.SourcePrimitiveIds,
            aggregate.Evidence);

    private static RoutingObstacle CreateCandidateObstacle(
        ObjectCandidate candidate,
        ObjectRoutingInfluence routingInfluence,
        RoutingObstacleKind obstacleKind) =>
        new(
            $"routing-obstacle:{candidate.Id}",
            candidate.PageNumber,
            candidate.Id,
            RoutingSourceKind.ObjectCandidate,
            obstacleKind,
            routingInfluence,
            InferCandidateStructuralInfluence(candidate),
            candidate.Category,
            candidate.Kind,
            candidate.Bounds,
            candidate.Label ?? candidate.SymbolName ?? candidate.DetectedTag,
            candidate.RoomId,
            candidate.RoomLabel,
            false,
            Array.Empty<string>(),
            candidate.Confidence,
            candidate.SourcePrimitiveIds,
            candidate.Evidence);

    private static RoutingRoomUseHint CreateRoomUseHint(RoomRegion room) =>
        new(
            $"routing-room-use:{room.Id}",
            room.PageNumber,
            room.Id,
            RoutingSourceKind.Room,
            room.UseKind,
            room.Bounds,
            room.Id,
            room.Label,
            room.Confidence,
            room.LabelSourcePrimitiveIds,
            room.Evidence);

    private static RoutingRoomUseHint CreateAggregateRoomUseHint(ObjectAggregate aggregate) =>
        new(
            $"routing-room-use:{aggregate.Id}",
            aggregate.PageNumber,
            aggregate.Id,
            RoutingSourceKind.ObjectAggregate,
            aggregate.RoomUseEvidence,
            aggregate.Bounds,
            aggregate.RoomId,
            aggregate.RoomLabel,
            aggregate.Confidence,
            aggregate.SourcePrimitiveIds,
            aggregate.Evidence);

    private static RoutingRoomUseHint CreateCandidateRoomUseHint(
        ObjectCandidate candidate,
        RoomUseKind roomUseKind) =>
        new(
            $"routing-room-use:{candidate.Id}",
            candidate.PageNumber,
            candidate.Id,
            RoutingSourceKind.ObjectCandidate,
            roomUseKind,
            candidate.Bounds,
            candidate.RoomId,
            candidate.RoomLabel,
            candidate.Confidence,
            candidate.SourcePrimitiveIds,
            candidate.Evidence);

    private static IReadOnlyList<RoutingSuppressedObject> CreateSuppressedObjects(PlanScanResult result)
    {
        var candidateLookup = result.ObjectCandidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var suppressed = new List<RoutingSuppressedObject>();
        var seenCandidateIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var aggregate in result.ObjectAggregates
                     .Where(aggregate => aggregate.SuppressChildObjectsForRouting)
                     .OrderBy(aggregate => aggregate.PageNumber)
                     .ThenBy(aggregate => aggregate.Id, StringComparer.Ordinal))
        {
            foreach (var childObjectId in aggregate.ChildObjectIds
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Distinct(StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
            {
                if (!seenCandidateIds.Add(childObjectId)
                    || !candidateLookup.TryGetValue(childObjectId, out var candidate))
                {
                    continue;
                }

                suppressed.Add(CreateSuppressedObject(aggregate, candidate));
            }
        }

        return suppressed
            .OrderBy(item => item.PageNumber)
            .ThenBy(item => item.ObjectCandidateId, StringComparer.Ordinal)
            .ToArray();
    }

    private static RoutingSuppressedObject CreateSuppressedObject(
        ObjectAggregate aggregate,
        ObjectCandidate candidate)
    {
        var action = SuppressedActionFor(aggregate);
        var reason = aggregate.RoutingInfluence == ObjectRoutingInfluence.RoomUseEvidenceOnly
            ? RoutingSuppressionReason.AggregateRoomUseEvidenceOnly
            : RoutingSuppressionReason.ReplacedByObjectAggregate;
        var replacementRoutingObstacleId = action == RoutingSuppressedObjectAction.UseAggregateObstacle
            ? $"routing-obstacle:{aggregate.Id}"
            : null;
        var roomUseHintId = action == RoutingSuppressedObjectAction.UseAggregateRoomUseHint
            ? $"routing-room-use:{aggregate.Id}"
            : null;
        var confidence = new Confidence(Math.Clamp(
            (aggregate.Confidence.Value + candidate.Confidence.Value) / 2,
            0,
            1));
        var evidence = new List<string>
        {
            $"child object {candidate.Id} is represented by aggregate {aggregate.Id}",
            $"aggregate routing influence {aggregate.RoutingInfluence}",
            action switch
            {
                RoutingSuppressedObjectAction.UseAggregateObstacle => $"use aggregate obstacle {replacementRoutingObstacleId} instead of this child object",
                RoutingSuppressedObjectAction.UseAggregateRoomUseHint => $"use aggregate room-use hint {roomUseHintId} instead of treating this child object as an obstacle",
                RoutingSuppressedObjectAction.IgnoreForRouting => "ignore this child object for routing/loop generation",
                _ => "suppression action is unknown and should be reviewed"
            }
        };

        if (!string.IsNullOrWhiteSpace(aggregate.Label))
        {
            evidence.Add($"aggregate label {aggregate.Label}");
        }

        if (aggregate.RoomUseEvidence is not RoomUseKind.Unknown)
        {
            evidence.Add($"aggregate room-use evidence {aggregate.RoomUseEvidence}");
        }

        if (!string.IsNullOrWhiteSpace(candidate.SymbolName))
        {
            evidence.Add($"child symbol {candidate.SymbolName}");
        }

        return new RoutingSuppressedObject(
            $"routing-suppression:{candidate.Id}",
            candidate.PageNumber,
            candidate.Id,
            aggregate.Id,
            reason,
            action,
            replacementRoutingObstacleId,
            roomUseHintId,
            aggregate.RoutingInfluence,
            aggregate.StructuralInfluence,
            candidate.Category,
            candidate.Kind,
            candidate.Bounds,
            candidate.Label ?? candidate.SymbolName ?? candidate.DetectedTag,
            candidate.RoomId ?? aggregate.RoomId,
            candidate.RoomLabel ?? aggregate.RoomLabel,
            confidence,
            candidate.SourcePrimitiveIds,
            evidence);
    }

    private static RoutingIgnoredObject CreateIgnoredObject(
        ObjectCandidate candidate,
        RoutingIgnoredObjectReason reason,
        RoutingSuppressedObject? suppressedObject)
    {
        var influence = InferCandidateRoutingInfluence(candidate);
        var roomUseKind = InferCandidateRoomUseHint(candidate);
        string? roomUseHintId;
        if (reason == RoutingIgnoredObjectReason.SuppressedByAggregate)
        {
            roomUseHintId = suppressedObject?.RoomUseHintId;
        }
        else if (!string.IsNullOrWhiteSpace(suppressedObject?.RoomUseHintId))
        {
            roomUseHintId = suppressedObject.RoomUseHintId;
        }
        else
        {
            roomUseHintId = roomUseKind is not RoomUseKind.Unknown
                ? $"routing-room-use:{candidate.Id}"
                : null;
        }
        var evidence = new List<string>
        {
            reason switch
            {
                RoutingIgnoredObjectReason.SuppressedByAggregate =>
                    $"object candidate {candidate.Id} is ignored as a standalone routing item because aggregate {suppressedObject?.SuppressedByAggregateId ?? "-"} represents it",
                RoutingIgnoredObjectReason.RoomUseEvidenceOnly =>
                    $"object candidate {candidate.Id} contributes room-use evidence {roomUseKind} instead of a routing obstacle",
                RoutingIgnoredObjectReason.ExplicitlyIgnored =>
                    $"object candidate {candidate.Id} has routing influence Ignore",
                RoutingIgnoredObjectReason.UnclassifiedReviewCandidate =>
                    $"object candidate {candidate.Id} is {candidate.Category} and needs review or a deterministic label before it can affect routing",
                RoutingIgnoredObjectReason.UnknownRoutingInfluence =>
                    $"object candidate {candidate.Id} has no deterministic routing influence",
                _ => $"object candidate {candidate.Id} is ignored for routing and should be reviewed"
            },
            $"candidate category {candidate.Category}",
            $"candidate kind {candidate.Kind}",
            $"inferred routing influence {influence}",
            $"inferred structural influence {InferCandidateStructuralInfluence(candidate)}"
        };

        if (!string.IsNullOrWhiteSpace(roomUseHintId))
        {
            evidence.Add($"related room-use hint {roomUseHintId}");
        }

        if (!string.IsNullOrWhiteSpace(suppressedObject?.Id))
        {
            evidence.Add($"related suppression record {suppressedObject.Id}");
        }

        if (!string.IsNullOrWhiteSpace(candidate.SymbolName))
        {
            evidence.Add($"symbol {candidate.SymbolName}");
        }

        if (!string.IsNullOrWhiteSpace(candidate.DetectedTag))
        {
            evidence.Add($"detected tag {candidate.DetectedTag}");
        }

        evidence.AddRange(candidate.Evidence);

        return new RoutingIgnoredObject(
            $"routing-ignored:{candidate.Id}",
            candidate.PageNumber,
            candidate.Id,
            reason,
            influence,
            InferCandidateStructuralInfluence(candidate),
            candidate.Category,
            candidate.Kind,
            candidate.SourceKind,
            candidate.SourceWallComponentId,
            candidate.SourceWallComponentKind,
            candidate.Bounds,
            candidate.Label ?? candidate.SymbolName ?? candidate.DetectedTag,
            candidate.RoomId,
            candidate.RoomLabel,
            suppressedObject?.Id,
            suppressedObject?.SuppressedByAggregateId,
            roomUseHintId,
            candidate.Confidence,
            candidate.SourcePrimitiveIds,
            evidence);
    }

    private static ObjectRoutingInfluence InferCandidateRoutingInfluence(ObjectCandidate candidate) =>
        candidate.Category switch
        {
            ObjectCategory.TextLabel => ObjectRoutingInfluence.Ignore,
            ObjectCategory.Vehicle => ObjectRoutingInfluence.RoomUseEvidenceOnly,
            ObjectCategory.Furniture => ObjectRoutingInfluence.SoftObstacle,
            ObjectCategory.GenericSymbol or ObjectCategory.Unknown => ObjectRoutingInfluence.Unknown,
            ObjectCategory.Stair
                or ObjectCategory.Elevator
                or ObjectCategory.Column
                or ObjectCategory.Shaft
                or ObjectCategory.Structural => ObjectRoutingInfluence.StructuralBarrier,
            ObjectCategory.Fixture
                or ObjectCategory.PlumbingFixture
                or ObjectCategory.ElectricalDevice
                or ObjectCategory.Lighting
                or ObjectCategory.HVACEquipment
                or ObjectCategory.FireSafety
                or ObjectCategory.Equipment => ObjectRoutingInfluence.HardObstacle,
            _ => ObjectRoutingInfluence.Unknown
        };

    private static RoutingIgnoredObjectReason IgnoredReasonFor(
        ObjectCandidate candidate,
        ObjectRoutingInfluence influence) =>
        influence switch
        {
            ObjectRoutingInfluence.Ignore => RoutingIgnoredObjectReason.ExplicitlyIgnored,
            ObjectRoutingInfluence.RoomUseEvidenceOnly => RoutingIgnoredObjectReason.RoomUseEvidenceOnly,
            ObjectRoutingInfluence.Unknown
                when candidate.Category is ObjectCategory.Unknown or ObjectCategory.GenericSymbol =>
                RoutingIgnoredObjectReason.UnclassifiedReviewCandidate,
            ObjectRoutingInfluence.Unknown => RoutingIgnoredObjectReason.UnknownRoutingInfluence,
            _ => RoutingIgnoredObjectReason.Unknown
        };

    private static RoutingSuppressedObjectAction SuppressedActionFor(ObjectAggregate aggregate) =>
        aggregate.RoutingInfluence switch
        {
            ObjectRoutingInfluence.RoomUseEvidenceOnly => RoutingSuppressedObjectAction.UseAggregateRoomUseHint,
            ObjectRoutingInfluence.SoftObstacle
                or ObjectRoutingInfluence.HardObstacle
                or ObjectRoutingInfluence.StructuralBarrier => RoutingSuppressedObjectAction.UseAggregateObstacle,
            ObjectRoutingInfluence.Ignore => RoutingSuppressedObjectAction.IgnoreForRouting,
            _ => RoutingSuppressedObjectAction.IgnoreForRouting
        };

    private static ObjectStructuralInfluence InferCandidateStructuralInfluence(ObjectCandidate candidate) =>
        candidate.Category switch
        {
            ObjectCategory.Stair
                or ObjectCategory.Elevator
                or ObjectCategory.Column
                or ObjectCategory.Shaft
                or ObjectCategory.Structural => ObjectStructuralInfluence.Structural,
            ObjectCategory.Fixture
                or ObjectCategory.PlumbingFixture
                or ObjectCategory.ElectricalDevice
                or ObjectCategory.Lighting
                or ObjectCategory.HVACEquipment
                or ObjectCategory.FireSafety
                or ObjectCategory.Equipment => ObjectStructuralInfluence.FixedEquipment,
            ObjectCategory.TextLabel or ObjectCategory.Vehicle => ObjectStructuralInfluence.None,
            ObjectCategory.Furniture or ObjectCategory.GenericSymbol or ObjectCategory.Unknown => ObjectStructuralInfluence.NonStructural,
            _ => ObjectStructuralInfluence.Unknown
        };

    private static RoomUseKind InferCandidateRoomUseHint(ObjectCandidate candidate) =>
        candidate.Category switch
        {
            ObjectCategory.Vehicle => RoomUseKind.Parking,
            ObjectCategory.HVACEquipment => RoomUseKind.HVAC,
            ObjectCategory.ElectricalDevice or ObjectCategory.Lighting => RoomUseKind.Electrical,
            ObjectCategory.PlumbingFixture => RoomUseKind.Plumbing,
            ObjectCategory.Stair => RoomUseKind.Stair,
            ObjectCategory.Elevator => RoomUseKind.Elevator,
            ObjectCategory.Shaft => RoomUseKind.Shaft,
            ObjectCategory.Equipment => RoomUseKind.Industrial,
            _ => RoomUseKind.Unknown
        };

    private static RoutingObstacleKind MapObstacleKind(ObjectRoutingInfluence routingInfluence) =>
        routingInfluence switch
        {
            ObjectRoutingInfluence.SoftObstacle => RoutingObstacleKind.SoftObstacle,
            ObjectRoutingInfluence.HardObstacle => RoutingObstacleKind.HardObstacle,
            ObjectRoutingInfluence.StructuralBarrier => RoutingObstacleKind.StructuralBarrier,
            _ => RoutingObstacleKind.Unknown
        };

    private static IReadOnlyDictionary<string, WallGraphComponent> BuildWallComponentLookup(
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

    private static RoutingBarrier[] BuildRoutingBarriers(
        PlanScanResult result,
        IReadOnlyDictionary<string, WallGraphComponent> wallComponentLookup)
    {
        var nodesById = result.WallGraph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var edgesByWallId = result.WallGraph.Edges
            .GroupBy(edge => edge.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var barriers = new List<RoutingBarrier>();

        foreach (var wall in result.Walls)
        {
            if (!edgesByWallId.TryGetValue(wall.Id, out var edges))
            {
                barriers.Add(CreateBarrier(wall, wallComponentLookup));
                continue;
            }

            var edgeBarriers = CreateCompressedGraphSpanBarriers(
                wall,
                edges,
                nodesById,
                edgesByWallId,
                result.Walls,
                wallComponentLookup,
                result.Calibration);
            if (edgeBarriers.Length == 0)
            {
                barriers.Add(CreateBarrier(wall, wallComponentLookup));
                continue;
            }

            barriers.AddRange(edgeBarriers);
        }

        return barriers.ToArray();
    }

    public static IReadOnlyList<RoutingDenseMinorDetailPattern> DetectDenseMinorRoutingDetailPatterns(PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var wallComponentLookup = BuildWallComponentLookup(result.WallGraph.Components);
        var nodesById = result.WallGraph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var wallLookup = result.Walls.ToDictionary(wall => wall.Id, StringComparer.Ordinal);
        var edgesByWallId = result.WallGraph.Edges
            .GroupBy(edge => edge.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var patterns = new List<RoutingDenseMinorDetailPattern>();
        var sequenceByPage = new Dictionary<int, int>();

        foreach (var hostWall in result.Walls)
        {
            if (!wallComponentLookup.TryGetValue(hostWall.Id, out var hostComponent)
                || hostComponent.Kind != WallGraphComponentKind.SecondaryStructural
                || !edgesByWallId.TryGetValue(hostWall.Id, out var hostEdges))
            {
                continue;
            }

            var minorIncidentWallIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var nodeId in hostEdges.SelectMany(edge => new[] { edge.FromNodeId, edge.ToNodeId }).Distinct(StringComparer.Ordinal))
            {
                if (!nodesById.TryGetValue(nodeId, out var node) || node.Kind != WallNodeKind.TJunction)
                {
                    continue;
                }

                foreach (var incidentWallId in IncidentWallIds(node, edgesByWallId).Where(id => id != hostWall.Id))
                {
                    if (!wallLookup.TryGetValue(incidentWallId, out var incidentWall)
                        || !wallComponentLookup.TryGetValue(incidentWallId, out var incidentComponent)
                        || incidentComponent.Kind != WallGraphComponentKind.SecondaryStructural
                        || !IsMinorPerpendicularRoutingDetail(hostWall, incidentWall, nodesById, node))
                    {
                        continue;
                    }

                    minorIncidentWallIds.Add(incidentWallId);
                }
            }

            if (minorIncidentWallIds.Count < DenseMinorRoutingPatternJunctionThreshold)
            {
                continue;
            }

            var pageSequence = sequenceByPage.TryGetValue(hostWall.PageNumber, out var lastSequence)
                ? lastSequence + 1
                : 1;
            sequenceByPage[hostWall.PageNumber] = pageSequence;
            var incidentWallIds = minorIncidentWallIds
                .Order(StringComparer.Ordinal)
                .ToArray();
            var wallIds = new[] { hostWall.Id }
                .Concat(incidentWallIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var patternWalls = wallIds
                .Select(id => wallLookup.TryGetValue(id, out var wall) ? wall : null)
                .OfType<WallSegment>()
                .ToArray();
            var sourcePrimitiveIds = patternWalls
                .SelectMany(wall => wall.SourcePrimitiveIds)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var bounds = PlanRect.Union(patternWalls.Select(wall => wall.Bounds));
            var confidence = patternWalls.Length == 0
                ? Confidence.Medium
                : new Confidence(Math.Min(
                    0.9,
                    patternWalls.Average(wall => wall.Confidence.Value) + 0.05));
            var evidence = new[]
            {
                $"secondary wall {hostWall.Id} has {incidentWallIds.Length} repeated minor perpendicular detail wall(s)",
                $"minor T-junction count {minorIncidentWallIds.Count}",
                "pattern is suppressed from trusted routing barriers but preserved as raw wall evidence",
                $"host wall component {hostComponent.Id} classified as {hostComponent.Kind}"
            };

            patterns.Add(new RoutingDenseMinorDetailPattern(
                $"routing-dense-minor-detail:p{hostWall.PageNumber}:{pageSequence}",
                hostWall.PageNumber,
                hostWall.Id,
                hostComponent.Id,
                hostComponent.Kind,
                incidentWallIds,
                wallIds,
                bounds,
                minorIncidentWallIds.Count,
                incidentWallIds.Length,
                confidence,
                sourcePrimitiveIds,
                evidence));
        }

        return patterns;
    }

    private static RoutingBarrier[] CreateCompressedGraphSpanBarriers(
        WallSegment wall,
        IReadOnlyList<WallEdge> edges,
        IReadOnlyDictionary<string, WallNode> nodesById,
        IReadOnlyDictionary<string, WallEdge[]> edgesByWallId,
        IReadOnlyList<WallSegment> walls,
        IReadOnlyDictionary<string, WallGraphComponent> wallComponentLookup,
        PlanCalibration calibration)
    {
        var wallLookup = walls.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var orderedNodes = edges
            .SelectMany(edge => new[] { edge.FromNodeId, edge.ToNodeId })
            .Distinct(StringComparer.Ordinal)
            .Select(id => nodesById.TryGetValue(id, out var node) ? node : null)
            .OfType<WallNode>()
            .Select(node => new RoutingWallNodeProjection(node, wall.CenterLine.ProjectParameter(node.Position)))
            .Where(item => item.Parameter >= -0.01 && item.Parameter <= 1.01)
            .OrderBy(item => item.Parameter)
            .ThenBy(item => item.Node.Id, StringComparer.Ordinal)
            .ToArray();
        orderedNodes = DeduplicateProjectedRoutingNodes(orderedNodes);
        if (orderedNodes.Length < 2)
        {
            return Array.Empty<RoutingBarrier>();
        }

        var splitNodes = orderedNodes
            .Where((item, index) => index == 0
                || index == orderedNodes.Length - 1
                || IsHardRoutingSplitNode(wall, item.Node, edgesByWallId, wallLookup, nodesById))
            .ToArray();
        if (splitNodes.Length < 2)
        {
            return Array.Empty<RoutingBarrier>();
        }

        var barriers = new List<RoutingBarrier>();
        for (var index = 1; index < splitNodes.Length; index++)
        {
            var from = splitNodes[index - 1];
            var to = splitNodes[index];
            var centerLine = new PlanLineSegment(from.Node.Position, to.Node.Position);
            if (centerLine.Length <= 0.5)
            {
                continue;
            }

            var compressedNodeCount = orderedNodes.Count(item =>
                item.Parameter > from.Parameter + 0.0001
                && item.Parameter < to.Parameter - 0.0001);
            barriers.Add(CreateGraphSpanBarrier(
                wall,
                $"routing-barrier:{wall.Id}:span:{index}",
                centerLine,
                from.Node.Id,
                to.Node.Id,
                compressedNodeCount,
                wallComponentLookup,
                calibration));
        }

        return barriers.ToArray();
    }

    private static RoutingWallNodeProjection[] DeduplicateProjectedRoutingNodes(IReadOnlyList<RoutingWallNodeProjection> nodes)
    {
        var deduped = new List<RoutingWallNodeProjection>();
        foreach (var node in nodes)
        {
            if (deduped.Any(existing => existing.Node.Position.DistanceTo(node.Node.Position) <= RoutingNodeMergeTolerance))
            {
                continue;
            }

            deduped.Add(node);
        }

        return deduped.ToArray();
    }

    private static bool IsHardRoutingSplitNode(
        WallSegment wall,
        WallNode node,
        IReadOnlyDictionary<string, WallEdge[]> edgesByWallId,
        IReadOnlyDictionary<string, WallSegment> wallLookup,
        IReadOnlyDictionary<string, WallNode> nodesById)
    {
        if (node.Kind == WallNodeKind.Inline)
        {
            return false;
        }

        if (node.Kind is WallNodeKind.Endpoint or WallNodeKind.Corner or WallNodeKind.Crossing or WallNodeKind.Junction)
        {
            return true;
        }

        if (node.Kind != WallNodeKind.TJunction)
        {
            return true;
        }

        var incidentWalls = IncidentWallIds(node, edgesByWallId)
            .Where(id => !string.Equals(id, wall.Id, StringComparison.Ordinal))
            .Select(id => wallLookup.TryGetValue(id, out var incidentWall) ? incidentWall : null)
            .OfType<WallSegment>()
            .ToArray();
        return incidentWalls.Length == 0
            || incidentWalls.Any(incidentWall => !IsMinorPerpendicularRoutingDetail(wall, incidentWall, nodesById, node));
    }

    private static IReadOnlyList<string> IncidentWallIds(
        WallNode node,
        IReadOnlyDictionary<string, WallEdge[]> edgesByWallId)
    {
        var ids = new List<string>();
        foreach (var (wallId, edges) in edgesByWallId)
        {
            if (edges.Any(edge =>
                string.Equals(edge.FromNodeId, node.Id, StringComparison.Ordinal)
                || string.Equals(edge.ToNodeId, node.Id, StringComparison.Ordinal)))
            {
                ids.Add(wallId);
            }
        }

        return ids;
    }

    private static bool IsMinorPerpendicularRoutingDetail(
        WallSegment hostWall,
        WallSegment incidentWall,
        IReadOnlyDictionary<string, WallNode> nodesById,
        WallNode node)
    {
        if (!IsNearPerpendicular(hostWall.CenterLine, incidentWall.CenterLine))
        {
            return false;
        }

        if (incidentWall.DrawingLength <= ShortUnreferencedRoutingBarrierDrawingLength
            || incidentWall.LengthMeters is > 0 and <= ShortUnreferencedRoutingBarrierLengthMeters)
        {
            return true;
        }

        return incidentWall.CenterLine.Start.DistanceTo(node.Position) <= RoutingNodeMergeTolerance
            && nodesById.Values.Count(other => other.Position.DistanceTo(incidentWall.CenterLine.End) <= RoutingNodeMergeTolerance) == 0
            || incidentWall.CenterLine.End.DistanceTo(node.Position) <= RoutingNodeMergeTolerance
            && nodesById.Values.Count(other => other.Position.DistanceTo(incidentWall.CenterLine.Start) <= RoutingNodeMergeTolerance) == 0;
    }

    private static bool IsNearPerpendicular(PlanLineSegment first, PlanLineSegment second)
    {
        var delta = Math.Abs(
            GeometryOperations.NormalizeAngleRadians(first.AngleRadians)
            - GeometryOperations.NormalizeAngleRadians(second.AngleRadians));
        delta = Math.Min(delta, Math.PI - delta);
        return Math.Abs(delta - (Math.PI / 2.0)) <= 0.16;
    }

    private static RoutingBarrier CreateGraphSpanBarrier(
        WallSegment wall,
        string id,
        PlanLineSegment centerLine,
        string fromNodeId,
        string toNodeId,
        int compressedNodeCount,
        IReadOnlyDictionary<string, WallGraphComponent> wallComponentLookup,
        PlanCalibration calibration)
    {
        wallComponentLookup.TryGetValue(wall.Id, out var component);
        var scaleGroup = calibration.SelectMeasurementScaleGroup(
            wall.PageNumber,
            centerLine.Bounds.Inflate(Math.Max(wall.Thickness / 2.0, 0.5)),
            wall.SourceRegionId);
        var evidence = new List<string>(wall.Evidence)
        {
            $"wall graph span from {fromNodeId} to {toNodeId}",
            "routing barrier split at hard wall graph junction nodes"
        };
        if (compressedNodeCount > 0)
        {
            evidence.Add($"compressed {compressedNodeCount} minor routing junction node(s)");
        }

        if (component is not null)
        {
            evidence.Add($"wall graph component {component.Id} classified as {component.Kind}");
            if (component.ExcludedFromStructuralTopology)
            {
                evidence.Add("component is excluded from structural topology and routing barriers");
            }
        }

        return new RoutingBarrier(
            id,
            wall.PageNumber,
            wall.Id,
            RoutingSourceKind.Wall,
            centerLine,
            centerLine.Bounds.Inflate(Math.Max(wall.Thickness / 2.0, 0.5)),
            wall.Thickness,
            centerLine.Length,
            calibration.ToMeters(centerLine.Length, scaleGroup),
            wall.ThicknessMillimeters,
            scaleGroup?.Id ?? wall.MeasurementScaleGroupId,
            component?.Id,
            component?.Kind,
            component?.ExcludedFromStructuralTopology ?? false,
            wall.Confidence,
            wall.SourcePrimitiveIds,
            evidence);
    }

    private static IReadOnlySet<string> BuildProtectedRoutingWallIds(PlanScanResult result)
    {
        var wallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in result.Rooms)
        {
            foreach (var wallId in room.WallIds)
            {
                AddIfPresent(wallIds, wallId);
            }
        }

        foreach (var opening in result.Openings)
        {
            foreach (var wallId in opening.HostWallIds)
            {
                AddIfPresent(wallIds, wallId);
            }

            if (opening.Placement is null)
            {
                continue;
            }

            AddIfPresent(wallIds, opening.Placement.HostWallId);
            foreach (var wallId in opening.Placement.AnchorWallIds)
            {
                AddIfPresent(wallIds, wallId);
            }
        }

        return wallIds;
    }

    private static IReadOnlySet<int> BuildStructuralComponentPages(IReadOnlyList<WallGraphComponent> components) =>
        components
            .Where(component => !component.ExcludedFromStructuralTopology)
            .Where(component => component.Kind is WallGraphComponentKind.MainStructural or WallGraphComponentKind.SecondaryStructural)
            .Select(component => component.PageNumber)
            .ToHashSet();

    private static IReadOnlySet<int> BuildRoomSolvedPages(IReadOnlyList<RoomRegion> rooms) =>
        rooms
            .Select(room => room.PageNumber)
            .ToHashSet();

    private static bool IsUnusedIsolatedRoutingBarrier(
        RoutingBarrier barrier,
        IReadOnlySet<string> protectedRoutingWallIds,
        IReadOnlySet<int> structuralComponentPages) =>
        barrier.WallComponentKind == WallGraphComponentKind.IsolatedFragment
        && structuralComponentPages.Contains(barrier.PageNumber)
        && !protectedRoutingWallIds.Contains(barrier.SourceId);

    private static bool IsUnusedShortStructuralRoutingBarrier(
        RoutingBarrier barrier,
        IReadOnlySet<string> protectedRoutingWallIds,
        IReadOnlySet<int> roomSolvedPages) =>
        barrier.WallComponentKind is WallGraphComponentKind.MainStructural or WallGraphComponentKind.SecondaryStructural
        && roomSolvedPages.Contains(barrier.PageNumber)
        && !protectedRoutingWallIds.Contains(barrier.SourceId)
        && IsShortRoutingBarrier(barrier);

    private static bool IsDenseMinorRoutingDetailBarrier(
        RoutingBarrier barrier,
        IReadOnlySet<string> denseMinorRoutingDetailWallIds) =>
        barrier.WallComponentKind == WallGraphComponentKind.SecondaryStructural
        && denseMinorRoutingDetailWallIds.Contains(barrier.SourceId);

    private static bool IsUnprotectedReviewEvidenceRoutingBarrier(
        RoutingBarrier barrier,
        IReadOnlySet<string> reviewRequiredEvidenceWallIds,
        IReadOnlySet<string> protectedRoutingWallIds) =>
        reviewRequiredEvidenceWallIds.Contains(barrier.SourceId)
        && !protectedRoutingWallIds.Contains(barrier.SourceId);

    private static bool IsShortRoutingBarrier(RoutingBarrier barrier) =>
        barrier.LengthMeters is > 0
            ? barrier.LengthMeters <= ShortUnreferencedRoutingBarrierLengthMeters
            : barrier.DrawingLength <= ShortUnreferencedRoutingBarrierDrawingLength;

    private static void AddIfPresent(HashSet<string> ids, string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            ids.Add(id);
        }
    }

    private readonly record struct RoutingWallNodeProjection(WallNode Node, double Parameter);
}
