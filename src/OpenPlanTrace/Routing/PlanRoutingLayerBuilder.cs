namespace OpenPlanTrace;

public static class PlanRoutingLayerBuilder
{
    public static PlanRoutingLayer FromScanResult(PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var wallComponentLookup = BuildWallComponentLookup(result.WallGraph.Components);
        var barriers = result.Walls
            .Select(wall => CreateBarrier(wall, wallComponentLookup))
            .Where(barrier => !barrier.ExcludedFromStructuralTopology)
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

    private static RoutingPassage CreatePassage(OpeningCandidate opening) =>
        new(
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
            opening.Placement,
            opening.Confidence,
            opening.SourcePrimitiveIds,
            opening.Evidence);

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
        var roomUseHintId = !string.IsNullOrWhiteSpace(suppressedObject?.RoomUseHintId)
            ? suppressedObject.RoomUseHintId
            : roomUseKind is not RoomUseKind.Unknown
                ? $"routing-room-use:{candidate.Id}"
                : null;
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
}
