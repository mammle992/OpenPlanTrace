namespace OpenPlanTrace;

internal sealed class ObjectAggregationStage : IPipelineStage
{
    public string Name => "object-aggregates";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        if (!context.Options.DetectObjectAggregates || context.ObjectCandidates.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        var sourceLookup = BuildSourceLookup(context.Document);
        var groupLookup = BuildObjectGroupLookup(context.ObjectGroups);
        var aggregates = new List<ObjectAggregate>();

        foreach (var pageGroup in context.ObjectCandidates
                     .Where(candidate => candidate.Kind != ObjectCandidateKind.TextLabel)
                     .GroupBy(candidate => candidate.PageNumber)
                     .OrderBy(group => group.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageNumber = pageGroup.Key;
            var candidates = pageGroup
                .OrderBy(candidate => candidate.Bounds.Top)
                .ThenBy(candidate => candidate.Bounds.Left)
                .ToArray();
            foreach (var cluster in BuildClusters(candidates, context.Options.ObjectAggregateClusterTolerance))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Qualifies(cluster, context, pageNumber))
                {
                    continue;
                }

                var aggregate = CreateAggregate(
                    cluster,
                    aggregates.Count + 1,
                    context,
                    sourceLookup,
                    groupLookup);
                if (aggregate is not null)
                {
                    aggregates.Add(aggregate);
                }
            }
        }

        context.ObjectAggregates.AddRange(aggregates);
        if (aggregates.Count > 0)
        {
            context.AddDiagnostic(
                "object_aggregates.detected",
                DiagnosticSeverity.Info,
                Name,
                $"Aggregated {aggregates.Sum(item => item.ChildObjectCount)} object child detection(s) into {aggregates.Count} compound object aggregate(s).",
                confidence: aggregates.Any(item => item.RequiresReview) ? Confidence.Medium : Confidence.High,
                scope: DiagnosticScope.Detection,
                sourcePrimitiveIds: aggregates.SelectMany(item => item.SourcePrimitiveIds),
                properties: new Dictionary<string, string>
                {
                    ["aggregateCount"] = aggregates.Count.ToString(),
                    ["childObjectCount"] = aggregates.Sum(item => item.ChildObjectCount).ToString(),
                    ["reviewAggregateCount"] = aggregates.Count(item => item.RequiresReview).ToString(),
                    ["roomUseEvidenceOnlyCount"] = aggregates.Count(item => item.RoutingInfluence == ObjectRoutingInfluence.RoomUseEvidenceOnly).ToString(),
                    ["routingInfluences"] = string.Join(",", aggregates.GroupBy(item => item.RoutingInfluence).Select(group => $"{group.Key}:{group.Count()}")),
                    ["categories"] = string.Join(",", aggregates.GroupBy(item => item.Category).Select(group => $"{group.Key}:{group.Count()}"))
                });
        }

        return ValueTask.CompletedTask;
    }

    private static IReadOnlyList<IReadOnlyList<ObjectCandidate>> BuildClusters(
        IReadOnlyList<ObjectCandidate> candidates,
        double tolerance)
    {
        var clusters = new List<IReadOnlyList<ObjectCandidate>>();
        var visited = new bool[candidates.Count];
        var searchTolerance = Math.Max(1, tolerance);

        for (var index = 0; index < candidates.Count; index++)
        {
            if (visited[index])
            {
                continue;
            }

            var cluster = new List<ObjectCandidate>();
            var queue = new Queue<int>();
            visited[index] = true;
            queue.Enqueue(index);

            while (queue.Count > 0)
            {
                var currentIndex = queue.Dequeue();
                var current = candidates[currentIndex];
                cluster.Add(current);

                for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                {
                    if (visited[candidateIndex])
                    {
                        continue;
                    }

                    var candidate = candidates[candidateIndex];
                    if (!SameRoomOrUnknown(current, candidate)
                        || !current.Bounds.Inflate(searchTolerance).Intersects(candidate.Bounds))
                    {
                        continue;
                    }

                    visited[candidateIndex] = true;
                    queue.Enqueue(candidateIndex);
                }
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    private static bool SameRoomOrUnknown(ObjectCandidate first, ObjectCandidate second)
    {
        if (string.IsNullOrWhiteSpace(first.RoomId) || string.IsNullOrWhiteSpace(second.RoomId))
        {
            return true;
        }

        return string.Equals(first.RoomId, second.RoomId, StringComparison.Ordinal);
    }

    private static bool Qualifies(
        IReadOnlyList<ObjectCandidate> cluster,
        ScanContext context,
        int pageNumber)
    {
        if (cluster.Count < Math.Max(2, context.Options.MinObjectAggregateChildCount))
        {
            return false;
        }

        var bounds = PlanRect.Union(cluster.Select(candidate => candidate.Bounds));
        if (bounds.IsEmpty || bounds.Area < 16 || bounds.Width < 2 || bounds.Height < 2)
        {
            return false;
        }

        var mainRegion = context.SheetRegions.FirstOrDefault(
            region => region.PageNumber == pageNumber && region.Kind == RegionKind.MainFloorPlan);
        if (mainRegion is null || mainRegion.Bounds.Area <= 0)
        {
            return false;
        }

        var room = FindDominantRoom(cluster, context);
        var maxArea = mainRegion.Bounds.Area * Math.Clamp(context.Options.MaxObjectAggregateAreaRatio, 0.005, 0.5);
        if (room is not null)
        {
            maxArea = Math.Min(maxArea, Math.Max(1, room.DrawingArea) * 0.45);
        }

        if (bounds.Area > maxArea
            || bounds.Width > mainRegion.Bounds.Width * 0.45
            || bounds.Height > mainRegion.Bounds.Height * 0.45)
        {
            return false;
        }

        return true;
    }

    private static ObjectAggregate? CreateAggregate(
        IReadOnlyList<ObjectCandidate> cluster,
        int aggregateNumber,
        ScanContext context,
        IReadOnlyDictionary<string, PrimitiveSourceMetadata> sourceLookup,
        IReadOnlyDictionary<string, IReadOnlyList<ObjectCandidateGroup>> groupLookup)
    {
        var ordered = cluster
            .OrderBy(candidate => candidate.PageNumber)
            .ThenBy(candidate => candidate.Bounds.Top)
            .ThenBy(candidate => candidate.Bounds.Left)
            .ToArray();
        var bounds = PlanRect.Union(ordered.Select(candidate => candidate.Bounds));
        var room = FindDominantRoom(ordered, context);
        var objectGroups = ordered
            .SelectMany(candidate => groupLookup.TryGetValue(candidate.Id, out var groups) ? groups : Array.Empty<ObjectCandidateGroup>())
            .DistinctBy(group => group.Id)
            .ToArray();
        var sourcePrimitiveIds = ordered
            .SelectMany(candidate => candidate.SourcePrimitiveIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sourceLayers = sourcePrimitiveIds
            .Select(sourceId => sourceLookup.TryGetValue(sourceId, out var source) ? source.Layer : null)
            .Where(layer => !string.IsNullOrWhiteSpace(layer))
            .Select(layer => layer!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var text = TextEvidence(ordered, room, sourceLayers);
        var category = ClassifyCategory(ordered, text, room);
        var kind = VisualAiCategoryMapper.KindFor(category);
        var routing = RoutingInfluenceFor(category);
        var structural = StructuralInfluenceFor(category);
        var roomUseEvidence = RoomUseEvidenceFor(category, room, text);
        var requiresReview = category is ObjectCategory.Unknown or ObjectCategory.GenericSymbol
            || routing == ObjectRoutingInfluence.Unknown;
        var label = LabelFor(category, text, ordered);
        var averageConfidence = ordered.Average(candidate => candidate.Confidence.Value);
        var confidence = new Confidence(Math.Clamp(
            averageConfidence
            + Math.Min(0.14, ordered.Length * 0.015)
            + (category is ObjectCategory.Unknown or ObjectCategory.GenericSymbol ? -0.05 : 0.08),
            0.35,
            0.95));
        var evidence = new List<string>
        {
            $"aggregated {ordered.Length} nearby object child detection(s) into one compound object",
            $"category {category}",
            $"routing influence {routing}",
            $"structural influence {structural}",
            routing == ObjectRoutingInfluence.RoomUseEvidenceOnly
                ? "aggregate is semantic room-use evidence only and should not block loop generation"
                : "aggregate can be consumed instead of individual child objects for downstream routing policy"
        };

        if (room is not null)
        {
            evidence.Add($"dominant room {room.Id} '{room.Label ?? room.UseKind.ToString()}'");
        }

        if (roomUseEvidence != RoomUseKind.Unknown)
        {
            evidence.Add($"room-use evidence {roomUseEvidence}");
        }

        if (sourceLayers.Length > 0)
        {
            evidence.Add($"source layers: {string.Join(", ", sourceLayers.Take(8))}");
        }

        var nearbyText = ordered
            .SelectMany(candidate => candidate.NearbyText)
            .Select(item => item.Text)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
        var composition = BuildComposition(ordered);
        if (nearbyText.Length > 0)
        {
            evidence.Add($"nearby text: {string.Join(", ", nearbyText.Take(6))}");
        }

        if (objectGroups.Length > 0)
        {
            evidence.Add($"intersects {objectGroups.Length} deterministic object group(s)");
        }

        if (composition.CategoryCounts.Count > 0)
        {
            evidence.Add($"child category makeup: {FormatCounts(composition.CategoryCounts)}");
        }

        if (composition.SourceKindCounts.Count > 0)
        {
            evidence.Add($"child source-kind makeup: {FormatCounts(composition.SourceKindCounts)}");
        }

        return new ObjectAggregate(
            $"object-aggregate:{aggregateNumber}",
            ordered[0].PageNumber,
            bounds,
            category,
            kind,
            ordered.Length,
            ordered.Select(candidate => candidate.Id).ToArray(),
            objectGroups.Select(group => group.Id).ToArray(),
            sourcePrimitiveIds,
            routing,
            structural,
            routing != ObjectRoutingInfluence.Unknown,
            roomUseEvidence,
            confidence,
            evidence)
        {
            Label = label,
            RoomId = room?.Id,
            RoomLabel = room?.Label,
            RequiresReview = requiresReview,
            NearbyText = nearbyText,
            SourceLayers = sourceLayers,
            Composition = composition
        };
    }

    private static ObjectAggregateComposition BuildComposition(IReadOnlyList<ObjectCandidate> candidates)
    {
        var sourceWallComponentIds = candidates
            .Select(candidate => candidate.SourceWallComponentId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new ObjectAggregateComposition(
            CountValues(candidates.Select(candidate => candidate.Category.ToString())),
            CountValues(candidates.Select(candidate => candidate.Kind.ToString())),
            CountValues(candidates.Select(candidate => candidate.SourceKind.ToString())),
            CountValues(candidates
                .Where(candidate => candidate.SourceWallComponentKind.HasValue)
                .Select(candidate => candidate.SourceWallComponentKind!.Value.ToString())),
            sourceWallComponentIds,
            candidates.Select(candidate => new ObjectAggregateChildObject(
                    candidate.Id,
                    candidate.Bounds,
                    candidate.Category,
                    candidate.Kind,
                    candidate.SourceKind,
                    candidate.SourceWallComponentId,
                    candidate.SourceWallComponentKind,
                    candidate.Label,
                    candidate.SymbolName,
                    candidate.DetectedTag,
                    candidate.Confidence,
                    candidate.SourcePrimitiveIds))
                .ToArray());
    }

    private static IReadOnlyList<ObjectAggregateCompositionCount> CountValues(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .GroupBy(value => value, StringComparer.Ordinal)
            .Select(group => new ObjectAggregateCompositionCount(group.Key, group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Value, StringComparer.Ordinal)
            .ToArray();

    private static string FormatCounts(IReadOnlyList<ObjectAggregateCompositionCount> counts) =>
        string.Join(", ", counts.Select(count => $"{count.Value}:{count.Count}"));

    private static ObjectCategory ClassifyCategory(
        IReadOnlyList<ObjectCandidate> candidates,
        string text,
        RoomRegion? room)
    {
        if (ContainsAny(text, "vehicle", "car", "auto", "automobile", "bil", "parkering", "parking")
            || (room?.UseKind == RoomUseKind.Parking && LooksVehicleShaped(candidates)))
        {
            return ObjectCategory.Vehicle;
        }

        var known = candidates
            .Where(candidate => candidate.Category is not ObjectCategory.Unknown and not ObjectCategory.GenericSymbol and not ObjectCategory.TextLabel)
            .GroupBy(candidate => candidate.Category)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();
        if (known is not null && known.Count() >= Math.Max(2, candidates.Count / 2))
        {
            return known.Key;
        }

        if (ContainsAny(text, "sofa", "chair", "table", "desk", "bed", "cabinet", "furniture", "stol", "bord"))
        {
            return ObjectCategory.Furniture;
        }

        if (ContainsAny(text, "pump", "valve", "tank", "compressor", "machine", "equipment", "motor"))
        {
            return ObjectCategory.Equipment;
        }

        return ObjectCategory.GenericSymbol;
    }

    private static bool LooksVehicleShaped(IReadOnlyList<ObjectCandidate> candidates)
    {
        var bounds = PlanRect.Union(candidates.Select(candidate => candidate.Bounds));
        if (bounds.Height <= 0)
        {
            return false;
        }

        var aspect = bounds.Width / bounds.Height;
        return aspect is >= 1.35 and <= 3.8
            || (1 / aspect) is >= 1.35 and <= 3.8;
    }

    private static ObjectRoutingInfluence RoutingInfluenceFor(ObjectCategory category) =>
        category switch
        {
            ObjectCategory.Vehicle => ObjectRoutingInfluence.RoomUseEvidenceOnly,
            ObjectCategory.Furniture => ObjectRoutingInfluence.SoftObstacle,
            ObjectCategory.Fixture
                or ObjectCategory.PlumbingFixture
                or ObjectCategory.ElectricalDevice
                or ObjectCategory.Lighting
                or ObjectCategory.HVACEquipment
                or ObjectCategory.FireSafety
                or ObjectCategory.Equipment => ObjectRoutingInfluence.HardObstacle,
            ObjectCategory.Stair
                or ObjectCategory.Elevator
                or ObjectCategory.Column
                or ObjectCategory.Shaft
                or ObjectCategory.Structural => ObjectRoutingInfluence.StructuralBarrier,
            _ => ObjectRoutingInfluence.Unknown
        };

    private static ObjectStructuralInfluence StructuralInfluenceFor(ObjectCategory category) =>
        category switch
        {
            ObjectCategory.Vehicle => ObjectStructuralInfluence.None,
            ObjectCategory.Furniture => ObjectStructuralInfluence.NonStructural,
            ObjectCategory.Fixture
                or ObjectCategory.PlumbingFixture
                or ObjectCategory.ElectricalDevice
                or ObjectCategory.Lighting
                or ObjectCategory.HVACEquipment
                or ObjectCategory.FireSafety
                or ObjectCategory.Equipment => ObjectStructuralInfluence.FixedEquipment,
            ObjectCategory.Stair
                or ObjectCategory.Elevator
                or ObjectCategory.Column
                or ObjectCategory.Shaft
                or ObjectCategory.Structural => ObjectStructuralInfluence.Structural,
            _ => ObjectStructuralInfluence.Unknown
        };

    private static RoomUseKind RoomUseEvidenceFor(ObjectCategory category, RoomRegion? room, string text)
    {
        if (category == ObjectCategory.Vehicle)
        {
            return RoomUseKind.Parking;
        }

        if (category is ObjectCategory.Equipment or ObjectCategory.HVACEquipment && ContainsAny(text, "pump", "machine", "mechanical", "teknisk"))
        {
            return RoomUseKind.Mechanical;
        }

        return room?.UseKind ?? RoomUseKind.Unknown;
    }

    private static string? LabelFor(
        ObjectCategory category,
        string text,
        IReadOnlyList<ObjectCandidate> candidates)
    {
        if (category == ObjectCategory.Vehicle)
        {
            return ContainsAny(text, "car", "bil", "auto", "automobile")
                ? "car"
                : "vehicle";
        }

        return candidates
            .Select(candidate => candidate.Label)
            .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label));
    }

    private static string TextEvidence(
        IReadOnlyList<ObjectCandidate> candidates,
        RoomRegion? room,
        IReadOnlyList<string> sourceLayers)
    {
        var parts = candidates
            .SelectMany(candidate => new[]
            {
                candidate.Label,
                candidate.SymbolName,
                candidate.DetectedTag,
                candidate.RoomLabel
            })
            .Concat(candidates.SelectMany(candidate => candidate.NearbyText.Select(text => text.Text)))
            .Concat(sourceLayers)
            .Concat(new[] { room?.Label, room?.UseKind.ToString() })
            .Where(item => !string.IsNullOrWhiteSpace(item));
        return string.Join(" ", parts).ToLowerInvariant();
    }

    private static bool ContainsAny(string text, params string[] terms) =>
        terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static RoomRegion? FindDominantRoom(
        IReadOnlyList<ObjectCandidate> candidates,
        ScanContext context)
    {
        var roomId = candidates
            .Select(candidate => candidate.RoomId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .GroupBy(id => id!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .FirstOrDefault();
        return roomId is null
            ? null
            : context.Rooms.FirstOrDefault(room => string.Equals(room.Id, roomId, StringComparison.Ordinal));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ObjectCandidateGroup>> BuildObjectGroupLookup(
        IReadOnlyList<ObjectCandidateGroup> groups)
    {
        var result = new Dictionary<string, List<ObjectCandidateGroup>>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            foreach (var candidateId in group.CandidateIds)
            {
                if (!result.TryGetValue(candidateId, out var bucket))
                {
                    bucket = new List<ObjectCandidateGroup>();
                    result[candidateId] = bucket;
                }

                bucket.Add(group);
            }
        }

        return result.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<ObjectCandidateGroup>)item.Value,
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, PrimitiveSourceMetadata> BuildSourceLookup(PlanDocument document)
    {
        var result = new Dictionary<string, PrimitiveSourceMetadata>(StringComparer.Ordinal);
        foreach (var page in document.Pages)
        {
            for (var index = 0; index < page.Primitives.Count; index++)
            {
                var primitive = page.Primitives[index];
                var metadata = primitive.Source with
                {
                    SourceId = Clean(primitive.Source.SourceId) ?? Clean(primitive.SourceId),
                    EntityType = Clean(primitive.Source.EntityType) ?? primitive.Kind.ToString(),
                    Layer = Clean(primitive.Source.Layer) ?? Clean(primitive.Layer)
                };
                Add(result, $"p{page.Number}:primitive:{index}", metadata);
                Add(result, primitive.SourceId, metadata);
                Add(result, primitive.Source.SourceId, metadata);
            }
        }

        return result;
    }

    private static void Add(
        IDictionary<string, PrimitiveSourceMetadata> result,
        string? key,
        PrimitiveSourceMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            result[key.Trim()] = metadata;
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
