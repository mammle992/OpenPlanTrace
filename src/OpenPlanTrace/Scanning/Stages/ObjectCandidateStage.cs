using System.Text.RegularExpressions;

namespace OpenPlanTrace;

internal sealed class ObjectCandidateStage : IPipelineStage
{
    public string Name => "object-candidates";

    private static readonly IReadOnlyDictionary<ObjectCategory, string[]> CategoryHints =
        new Dictionary<ObjectCategory, string[]>
        {
            [ObjectCategory.Stair] = new[] { "stair", "stairs", "staircase", "trapp", "step" },
            [ObjectCategory.Elevator] = new[] { "elevator", "lift", "heis", "elev" },
            [ObjectCategory.Column] = new[] { "column", "col", "pillar", "post" },
            [ObjectCategory.Shaft] = new[] { "shaft", "riser", "void" },
            [ObjectCategory.PlumbingFixture] = new[] { "toilet", "wc", "sink", "lav", "basin", "shower", "bath", "urinal", "vvs", "plumb" },
            [ObjectCategory.ElectricalDevice] = new[] { "elec", "electrical", "outlet", "socket", "switch", "panel", "panelboard", "power", "db", "mccb" },
            [ObjectCategory.Lighting] = new[] { "light", "lighting", "lamp", "fixture", "luminaire", "downlight" },
            [ObjectCategory.HVACEquipment] = new[] { "hvac", "ahu", "vav", "vent", "diffuser", "duct", "fan", "grille", "damper", "air", "supply", "return", "exhaust" },
            [ObjectCategory.FireSafety] = new[] { "fire", "sprinkler", "alarm", "extinguisher", "hose", "hydrant", "escape", "smoke", "detector" },
            [ObjectCategory.Equipment] = new[] { "equip", "equipment", "machine", "pump", "valve", "tank", "compressor", "motor", "process", "vessel" },
            [ObjectCategory.Vehicle] = new[] { "vehicle", "car", "auto", "automobile", "parking", "parkering", "garage", "garasje", "bil" },
            [ObjectCategory.Furniture] = new[] { "desk", "chair", "table", "sofa", "bed", "cabinet", "furn", "furniture" },
            [ObjectCategory.Structural] = new[] { "beam", "brace", "steel", "concrete", "struct" },
            [ObjectCategory.Fixture] = new[] { "fixture", "casework", "kitchen", "counter", "appliance" }
        };

    private static readonly IReadOnlyDictionary<string, ObjectCategory> IndustrialTagPrefixes =
        new Dictionary<string, ObjectCategory>(StringComparer.Ordinal)
        {
            ["P"] = ObjectCategory.Equipment,
            ["PU"] = ObjectCategory.Equipment,
            ["V"] = ObjectCategory.Equipment,
            ["HV"] = ObjectCategory.Equipment,
            ["XV"] = ObjectCategory.Equipment,
            ["CV"] = ObjectCategory.Equipment,
            ["SV"] = ObjectCategory.Equipment,
            ["PSV"] = ObjectCategory.Equipment,
            ["TK"] = ObjectCategory.Equipment,
            ["T"] = ObjectCategory.Equipment,
            ["VES"] = ObjectCategory.Equipment,
            ["HX"] = ObjectCategory.Equipment,
            ["HE"] = ObjectCategory.Equipment,
            ["PMP"] = ObjectCategory.Equipment,
            ["CMP"] = ObjectCategory.Equipment,
            ["AHU"] = ObjectCategory.HVACEquipment,
            ["VAV"] = ObjectCategory.HVACEquipment,
            ["FCU"] = ObjectCategory.HVACEquipment,
            ["RTU"] = ObjectCategory.HVACEquipment,
            ["EF"] = ObjectCategory.HVACEquipment,
            ["SF"] = ObjectCategory.HVACEquipment,
            ["RF"] = ObjectCategory.HVACEquipment,
            ["FAN"] = ObjectCategory.HVACEquipment,
            ["DB"] = ObjectCategory.ElectricalDevice,
            ["MCC"] = ObjectCategory.ElectricalDevice,
            ["MSB"] = ObjectCategory.ElectricalDevice,
            ["LP"] = ObjectCategory.ElectricalDevice,
            ["PP"] = ObjectCategory.ElectricalDevice,
            ["FA"] = ObjectCategory.FireSafety,
            ["FACP"] = ObjectCategory.FireSafety,
            ["SP"] = ObjectCategory.FireSafety
        };

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        if (!context.Options.DetectObjectCandidates)
        {
            return ValueTask.CompletedTask;
        }

        var consumedPrimitiveIds = context.Walls
            .SelectMany(wall => wall.SourcePrimitiveIds)
            .Concat(context.Openings.SelectMany(opening => opening.SourcePrimitiveIds))
            .Concat(context.Dimensions.SelectMany(dimension => dimension.SourcePrimitiveIds))
            .Concat(context.Annotations.SelectMany(annotation => annotation.SourcePrimitiveIds))
            .Concat(context.Annotations.SelectMany(annotation => annotation.Items.SelectMany(item => item.SourcePrimitiveIds)))
            .Concat(context.GridAxes.SelectMany(axis => axis.SourcePrimitiveIds))
            .Concat(context.GridAxes.SelectMany(axis => axis.LabelSourcePrimitiveIds))
            .Concat(context.Rooms.SelectMany(room => room.LabelSourcePrimitiveIds))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var page in context.Document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mainRegion = context.SheetRegions.FirstOrDefault(
                region => region.PageNumber == page.Number && region.Kind == RegionKind.MainFloorPlan);

            if (mainRegion is null)
            {
                continue;
            }

            var nearbyTextIndex = ObjectTextSpatialIndex.Create(page, context);
            for (var index = 0; index < page.Primitives.Count; index++)
            {
                var primitive = page.Primitives[index];
                var primitiveId = context.PrimitiveId(page.Number, index, primitive);
                if (consumedPrimitiveIds.Contains(primitiveId)
                    || !mainRegion.Bounds.Contains(primitive.Bounds.Center, context.Options.SheetMargin))
                {
                    continue;
                }

                var candidate = TryCreateCandidate(page, primitive, primitiveId, context, nearbyTextIndex);
                if (candidate is not null)
                {
                    context.ObjectCandidates.Add(candidate);
                    consumedPrimitiveIds.Add(primitiveId);
                }
            }

            AddCompositeLineworkCandidates(page, mainRegion, context, consumedPrimitiveIds, nearbyTextIndex);
            AddObjectLikeWallComponentCandidates(page, mainRegion, context, nearbyTextIndex);
        }

        return ValueTask.CompletedTask;
    }

    private static ObjectCandidate? TryCreateCandidate(
        PlanPage page,
        PlanPrimitive primitive,
        string primitiveId,
        ScanContext context,
        ObjectTextSpatialIndex nearbyTextIndex)
    {
        var pageNumber = page.Number;
        var mainArea = context.SheetRegions
            .First(region => region.PageNumber == pageNumber && region.Kind == RegionKind.MainFloorPlan)
            .Bounds
            .Area;

        var room = FindContainingRoom(pageNumber, primitive.Bounds.Center, context);
        var classification = Classify(primitive);

        if (primitive is TextPrimitive text && text.Text.Trim().Length > 0)
        {
            if (LooksLikeAnnotationOrDimension(text.Text))
            {
                return null;
            }

            var candidate = new ObjectCandidate(
                $"page:{pageNumber}:object:{context.ObjectCandidates.Count + 1}",
                pageNumber,
                ObjectCandidateKind.TextLabel,
                text.Bounds,
                new Confidence(0.42))
            {
                Label = text.Text.Trim(),
                Category = ObjectCategory.TextLabel,
                SourceKind = ObjectCandidateSourceKind.TextPrimitive,
                SourcePrimitiveIds = new[] { primitiveId },
                Evidence = new[] { "unconsumed text inside main floorplan" }
            };

            return AttachNearbyText(
                page,
                context,
                Decorate(candidate, room),
                nearbyTextIndex,
                includeForTextCandidate: false);
        }

        if (primitive is SymbolPrimitive symbol)
        {
            var category = classification.Category == ObjectCategory.Unknown
                ? ObjectCategory.GenericSymbol
                : classification.Category;
            var confidence = new Confidence(Math.Min(0.88, 0.56 + classification.Score));

            var candidate = new ObjectCandidate(
                $"page:{pageNumber}:object:{context.ObjectCandidates.Count + 1}",
                pageNumber,
                KindFor(category),
                symbol.Bounds,
                confidence)
            {
                Label = symbol.Name,
                SymbolName = symbol.Name,
                SourceKind = ObjectCandidateSourceKind.CadSymbol,
                DetectedTag = Clean(classification.DetectedTag),
                DetectedTagSourcePrimitiveId = Clean(classification.DetectedTag) is null ? null : primitiveId,
                Category = category,
                SourcePrimitiveIds = new[] { primitiveId },
                Evidence = classification.Evidence.Count == 0
                    ? new[] { "CAD/block symbol primitive" }
                    : classification.Evidence.Prepend("CAD/block symbol primitive").ToArray()
            };

            return AttachNearbyText(
                page,
                context,
                Decorate(candidate, room),
                nearbyTextIndex);
        }

        if (primitive is RectanglePrimitive or PolylinePrimitive { Closed: true })
        {
            var area = primitive.Bounds.Area;
            if (area >= 25 && area <= mainArea * 0.05)
            {
                var category = classification.Category == ObjectCategory.Unknown
                    ? ObjectCategory.GenericSymbol
                    : classification.Category;
                var confidence = new Confidence(Math.Min(0.78, 0.46 + classification.Score));

                var candidate = new ObjectCandidate(
                    $"page:{pageNumber}:object:{context.ObjectCandidates.Count + 1}",
                    pageNumber,
                    KindFor(category),
                    primitive.Bounds,
                    confidence)
                {
                    Category = category,
                    SourceKind = ObjectCandidateSourceKind.ClosedGeometry,
                    DetectedTag = Clean(classification.DetectedTag),
                    DetectedTagSourcePrimitiveId = Clean(classification.DetectedTag) is null ? null : primitiveId,
                    SourcePrimitiveIds = new[] { primitiveId },
                    Evidence = classification.Evidence.Count == 0
                        ? new[] { "compact closed geometry inside main floorplan" }
                        : classification.Evidence.Prepend("compact closed geometry inside main floorplan").ToArray()
                };

                return AttachNearbyText(
                    page,
                    context,
                    Decorate(candidate, room),
                    nearbyTextIndex);
            }
        }

        return null;
    }

    private static void AddCompositeLineworkCandidates(
        PlanPage page,
        SheetRegion mainRegion,
        ScanContext context,
        HashSet<string> consumedPrimitiveIds,
        ObjectTextSpatialIndex nearbyTextIndex)
    {
        if (!context.Options.DetectCompositeObjectCandidates)
        {
            return;
        }

        var uncappedItems = page.Primitives
            .Select((primitive, index) => LineworkItem.TryCreate(
                page.Number,
                index,
                primitive,
                context,
                consumedPrimitiveIds,
                mainRegion.Bounds))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
        var items = LimitCompositeLineworkItems(uncappedItems, page.Number, mainRegion, context);
        if (items.Length < context.Options.MinCompositeObjectPrimitiveCount)
        {
            return;
        }

        var clusters = BuildLineworkClusters(items, context.Options.CompositeObjectClusterTolerance)
            .Select(cluster => cluster.OrderBy(item => item.Bounds.Top).ThenBy(item => item.Bounds.Left).ToArray())
            .OrderBy(cluster => PlanRect.Union(cluster.Select(item => item.Bounds)).Top)
            .ThenBy(cluster => PlanRect.Union(cluster.Select(item => item.Bounds)).Left)
            .ToArray();

        var added = new List<ObjectCandidate>();
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cluster in clusters)
        {
            if (added.Count >= context.Options.MaxCompositeObjectCandidatesPerPage)
            {
                AddCompositeObjectLimitDiagnostic(page.Number, mainRegion, added.Count, context);
                break;
            }

            if (!QualifiesCompositeCluster(cluster, mainRegion.Bounds, context.Options))
            {
                continue;
            }

            var candidate = CreateCompositeLineworkCandidate(page, cluster, context, nearbyTextIndex);
            context.ObjectCandidates.Add(candidate);
            added.Add(candidate);
            foreach (var sourceId in candidate.SourcePrimitiveIds)
            {
                consumedPrimitiveIds.Add(sourceId);
                sourceIds.Add(sourceId);
            }
        }

        if (added.Count == 0)
        {
            return;
        }

        context.AddDiagnostic(
            "objects.composite_linework.detected",
            DiagnosticSeverity.Info,
            "object-candidates",
            $"Detected {added.Count} composite object candidate(s) from compact unconsumed linework islands.",
            page.Number,
            PlanRect.Union(added.Select(candidate => candidate.Bounds)),
            added.Any(candidate => candidate.Category == ObjectCategory.GenericSymbol)
                ? Confidence.Medium
                : Confidence.High,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: sourceIds,
            properties: new Dictionary<string, string>
            {
                ["candidateCount"] = added.Count.ToString(),
                ["sourcePrimitiveCount"] = sourceIds.Count.ToString(),
                ["reviewCandidateCount"] = added.Count(candidate => candidate.Category == ObjectCategory.GenericSymbol).ToString(),
                ["categories"] = string.Join(",", added.GroupBy(candidate => candidate.Category).Select(group => $"{group.Key}:{group.Count()}")),
                ["sourceRegionId"] = mainRegion.Id
            });
    }

    private static void AddObjectLikeWallComponentCandidates(
        PlanPage page,
        SheetRegion mainRegion,
        ScanContext context,
        ObjectTextSpatialIndex nearbyTextIndex)
    {
        var components = context.WallGraph.Components
            .Where(component => component.PageNumber == page.Number)
            .Where(component => component.Kind == WallGraphComponentKind.ObjectLikeIsland)
            .Where(component => component.ExcludedFromStructuralTopology)
            .Where(component => !component.Bounds.IsEmpty)
            .Where(component => mainRegion.Bounds.Contains(component.Bounds.Center, context.Options.SheetMargin))
            .OrderBy(component => component.Bounds.Top)
            .ThenBy(component => component.Bounds.Left)
            .ThenBy(component => component.Id, StringComparer.Ordinal)
            .ToArray();

        if (components.Length == 0)
        {
            return;
        }

        var added = new List<ObjectCandidate>();
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            var candidate = CreateObjectLikeWallComponentCandidate(page, component, context, nearbyTextIndex);
            context.ObjectCandidates.Add(candidate);
            added.Add(candidate);
            foreach (var sourceId in candidate.SourcePrimitiveIds)
            {
                sourceIds.Add(sourceId);
            }
        }

        context.AddDiagnostic(
            "objects.wall_component_islands.promoted",
            DiagnosticSeverity.Info,
            "object-candidates",
            $"Promoted {added.Count} topology-excluded wall component(s) into reviewable object candidates.",
            page.Number,
            PlanRect.Union(added.Select(candidate => candidate.Bounds)),
            Confidence.Medium,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: sourceIds,
            properties: new Dictionary<string, string>
            {
                ["candidateCount"] = added.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["componentIds"] = string.Join(",", components.Select(component => component.Id).Take(20)),
                ["sourcePrimitiveCount"] = sourceIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sourceRegionId"] = mainRegion.Id
            });
    }

    private static ObjectCandidate CreateObjectLikeWallComponentCandidate(
        PlanPage page,
        WallGraphComponent component,
        ScanContext context,
        ObjectTextSpatialIndex nearbyTextIndex)
    {
        var evidence = new List<string>
        {
            $"wall graph component {component.Id} classified as {component.Kind}",
            "component excluded from structural topology",
            $"{component.WallCount} wall-like segment(s), {component.NodeCount} node(s), {component.EdgeCount} edge(s)",
            $"drawing length {component.DrawingLength.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"
        };
        evidence.AddRange(component.Evidence);

        var confidence = new Confidence(Math.Clamp(component.Confidence.Value - 0.08, 0.42, 0.68));
        var candidate = new ObjectCandidate(
            $"page:{page.Number}:object:{context.ObjectCandidates.Count + 1}",
            page.Number,
            ObjectCandidateKind.Symbol,
            component.Bounds,
            confidence)
        {
            Category = ObjectCategory.GenericSymbol,
            SourceKind = ObjectCandidateSourceKind.WallComponentIsland,
            SourceWallComponentId = component.Id,
            SourceWallComponentKind = component.Kind,
            SourcePrimitiveIds = component.SourcePrimitiveIds
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Evidence = evidence.Distinct(StringComparer.Ordinal).ToArray()
        };

        return AttachNearbyText(
            page,
            context,
            Decorate(candidate, FindContainingRoom(page.Number, component.Bounds.Center, context)),
            nearbyTextIndex);
    }

    private static LineworkItem[] LimitCompositeLineworkItems(
        IReadOnlyList<LineworkItem> items,
        int pageNumber,
        SheetRegion mainRegion,
        ScanContext context)
    {
        var limit = context.Options.MaxCompositeObjectPrimitiveSearchCount;
        if (limit <= 0 || items.Count <= limit)
        {
            return items.ToArray();
        }

        var kept = items
            .OrderByDescending(LineworkPriority)
            .ThenBy(item => item.Length)
            .ThenBy(item => item.PrimitiveId, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        var keptIds = kept.Select(item => item.PrimitiveId).ToHashSet(StringComparer.Ordinal);
        var skipped = items.Where(item => !keptIds.Contains(item.PrimitiveId)).ToArray();

        context.AddDiagnostic(
            "objects.composite_linework.primitive_limit_applied",
            DiagnosticSeverity.Warning,
            "object-candidates",
            "Composite object linework search exceeded the configured per-page primitive limit; object-like and compact primitives were kept.",
            pageNumber,
            mainRegion.Bounds,
            Confidence.Medium,
            scope: DiagnosticScope.Page,
            sourcePrimitiveIds: skipped.Select(item => item.PrimitiveId).Take(50),
            properties: new Dictionary<string, string>
            {
                ["eligiblePrimitiveCountBeforeLimit"] = items.Count.ToString(),
                ["keptPrimitiveCount"] = kept.Length.ToString(),
                ["skippedPrimitiveCount"] = skipped.Length.ToString(),
                ["maxCompositeObjectPrimitiveSearchCount"] = limit.ToString(),
                ["sourceRegionId"] = mainRegion.Id
            });

        return kept;
    }

    private static int LineworkPriority(LineworkItem item) =>
        item.LayerCategory switch
        {
            LayerCategory.Equipment or LayerCategory.Electrical or LayerCategory.HVAC or LayerCategory.Plumbing or LayerCategory.FireSafety => 4,
            LayerCategory.Structural => 3,
            LayerCategory.Unknown => 2,
            _ => 1
        };

    private static IReadOnlyList<IReadOnlyList<LineworkItem>> BuildLineworkClusters(
        IReadOnlyList<LineworkItem> items,
        double tolerance)
    {
        var clusters = new List<IReadOnlyList<LineworkItem>>();
        var visited = new bool[items.Count];
        var searchTolerance = Math.Max(0.1, tolerance);
        var spatialIndex = LineworkSpatialIndex.Create(items, searchTolerance);

        for (var index = 0; index < items.Count; index++)
        {
            if (visited[index])
            {
                continue;
            }

            var cluster = new List<LineworkItem>();
            var queue = new Queue<int>();
            visited[index] = true;
            queue.Enqueue(index);

            while (queue.Count > 0)
            {
                var currentIndex = queue.Dequeue();
                var current = items[currentIndex];
                cluster.Add(current);

                foreach (var candidateIndex in spatialIndex.Query(current.Bounds.Inflate(searchTolerance)))
                {
                    if (visited[candidateIndex])
                    {
                        continue;
                    }

                    if (!current.Bounds.Inflate(searchTolerance).Intersects(items[candidateIndex].Bounds))
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

    private static bool QualifiesCompositeCluster(
        IReadOnlyList<LineworkItem> cluster,
        PlanRect mainRegionBounds,
        ScannerOptions options)
    {
        if (cluster.Count < Math.Max(2, options.MinCompositeObjectPrimitiveCount))
        {
            return false;
        }

        var bounds = PlanRect.Union(cluster.Select(item => item.Bounds));
        if (bounds.IsEmpty || bounds.Area < 9)
        {
            return false;
        }

        if (bounds.Width < 2 || bounds.Height < 2)
        {
            return false;
        }

        var maxArea = mainRegionBounds.Area * Math.Clamp(options.MaxCompositeObjectAreaRatio, 0.001, 0.5);
        if (bounds.Area > maxArea)
        {
            return false;
        }

        if (bounds.Width > mainRegionBounds.Width * 0.45 || bounds.Height > mainRegionBounds.Height * 0.45)
        {
            return false;
        }

        return true;
    }

    private static ObjectCandidate CreateCompositeLineworkCandidate(
        PlanPage page,
        IReadOnlyList<LineworkItem> cluster,
        ScanContext context,
        ObjectTextSpatialIndex nearbyTextIndex)
    {
        var pageNumber = page.Number;
        var bounds = PlanRect.Union(cluster.Select(item => item.Bounds));
        var classification = ClassifyComposite(cluster);
        var category = classification.Category == ObjectCategory.Unknown
            ? ObjectCategory.GenericSymbol
            : classification.Category;
        var confidence = new Confidence(Math.Clamp(
            0.48
            + classification.Score
            + Math.Min(0.12, cluster.Count * 0.02),
            0.42,
            category == ObjectCategory.GenericSymbol ? 0.68 : 0.86));
        var sourceLayers = cluster
            .Select(item => item.LayerName)
            .Where(layer => !string.IsNullOrWhiteSpace(layer) && layer != LayerAnalyzer.UnlayeredName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(layer => layer, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var evidence = new List<string>
        {
            "composite linework object island",
            $"{cluster.Count} unconsumed primitive(s)",
            $"bounds {Math.Round(bounds.Width, 3)} x {Math.Round(bounds.Height, 3)} drawing units"
        };

        if (sourceLayers.Length > 0)
        {
            evidence.Add($"source layers: {string.Join(", ", sourceLayers)}");
        }

        evidence.AddRange(classification.Evidence);

        var candidate = new ObjectCandidate(
            $"page:{pageNumber}:object:{context.ObjectCandidates.Count + 1}",
            pageNumber,
            KindFor(category),
            bounds,
            confidence)
        {
            Category = category,
            SourceKind = ObjectCandidateSourceKind.CompositeLinework,
            DetectedTag = Clean(classification.DetectedTag),
            DetectedTagSourcePrimitiveId = Clean(classification.DetectedTag) is null
                ? null
                : cluster.Select(item => item.PrimitiveId).FirstOrDefault(),
            SourcePrimitiveIds = cluster
                .Select(item => item.PrimitiveId)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Evidence = evidence.Distinct(StringComparer.Ordinal).ToArray()
        };

        return AttachNearbyText(
            page,
            context,
            Decorate(candidate, FindContainingRoom(pageNumber, bounds.Center, context)),
            nearbyTextIndex);
    }

    private static ObjectClassification ClassifyComposite(IReadOnlyList<LineworkItem> cluster)
    {
        var direct = cluster
            .Select(item => Classify(item.Primitive))
            .Where(classification => classification.Category != ObjectCategory.Unknown)
            .OrderByDescending(classification => classification.Score)
            .FirstOrDefault();

        if (direct is not null)
        {
            return direct;
        }

        var layerClassification = cluster
            .Select(item => new
            {
                Item = item,
                Category = CategoryFromLayer(item.LayerCategory)
            })
            .Where(item => item.Category != ObjectCategory.Unknown)
            .Select(item => new ObjectClassification(
                item.Category,
                Math.Min(0.2, item.Item.LayerConfidence.Value * 0.24),
                new[] { $"layer {item.Item.LayerName} classified {item.Item.LayerCategory}" }))
            .OrderByDescending(classification => classification.Score)
            .FirstOrDefault();

        return layerClassification ?? new ObjectClassification(
            ObjectCategory.Unknown,
            0,
            new[] { "no reliable symbol/category text; review as generic linework symbol" });
    }

    private static ObjectCategory CategoryFromLayer(LayerCategory layerCategory) =>
        layerCategory switch
        {
            LayerCategory.Structural => ObjectCategory.Structural,
            LayerCategory.Equipment => ObjectCategory.Equipment,
            LayerCategory.Electrical => ObjectCategory.ElectricalDevice,
            LayerCategory.HVAC => ObjectCategory.HVACEquipment,
            LayerCategory.Plumbing => ObjectCategory.PlumbingFixture,
            LayerCategory.FireSafety => ObjectCategory.FireSafety,
            _ => ObjectCategory.Unknown
        };

    private static void AddCompositeObjectLimitDiagnostic(
        int pageNumber,
        SheetRegion mainRegion,
        int addedForPage,
        ScanContext context)
    {
        context.AddDiagnostic(
            "objects.composite_linework.limit_reached",
            DiagnosticSeverity.Warning,
            "object-candidates",
            "Composite object candidate generation reached the configured per-page limit.",
            pageNumber,
            mainRegion.Bounds,
            Confidence.Medium,
            scope: DiagnosticScope.Page,
            properties: new Dictionary<string, string>
            {
                ["maxCompositeObjectCandidatesPerPage"] = context.Options.MaxCompositeObjectCandidatesPerPage.ToString(),
                ["addedForPage"] = addedForPage.ToString(),
                ["sourceRegionId"] = mainRegion.Id
            });
    }

    private static ObjectCandidate Decorate(ObjectCandidate candidate, RoomRegion? room) =>
        room is null
            ? candidate
            : candidate with
            {
                RoomId = room.Id,
                RoomLabel = room.Label,
                Evidence = candidate.Evidence
                    .Append(string.IsNullOrWhiteSpace(room.Label)
                        ? $"assigned to room {room.Id}"
                        : $"assigned to room {room.Label}")
                    .ToArray()
            };

    private static ObjectCandidate AttachNearbyText(
        PlanPage page,
        ScanContext context,
        ObjectCandidate candidate,
        ObjectTextSpatialIndex nearbyTextIndex,
        bool includeForTextCandidate = true)
    {
        if (!includeForTextCandidate
            || context.Options.MaxNearbyTextPerObject <= 0
            || context.Options.ObjectNearbyTextSearchRadius <= 0)
        {
            return candidate;
        }

        var nearby = NearbyText(page, candidate, context, nearbyTextIndex).ToArray();
        if (nearby.Length == 0)
        {
            return candidate;
        }

        var labels = nearby
            .Select(item => item.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        var evidence = candidate.Evidence
            .Append($"nearby text: {string.Join(", ", labels)}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var result = candidate with
        {
            NearbyText = nearby,
            Evidence = evidence
        };

        return ApplyNearbyTextClassification(result, nearby, context);
    }

    private static ObjectCandidate ApplyNearbyTextClassification(
        ObjectCandidate candidate,
        IReadOnlyList<ObjectNearbyText> nearby,
        ScanContext context)
    {
        var classificationNearby = nearby
            .Where(item => !IsRoomLabelNearbyText(item, context))
            .ToArray();
        var classification = ClassifyNearbyText(classificationNearby);
        if (candidate.Category is not (ObjectCategory.Unknown or ObjectCategory.GenericSymbol))
        {
            if (string.IsNullOrWhiteSpace(candidate.DetectedTag)
                && !string.IsNullOrWhiteSpace(classification.DetectedTag))
            {
                return candidate with
                {
                    DetectedTag = Clean(classification.DetectedTag),
                    DetectedTagSourcePrimitiveId = Clean(classification.DetectedTagSourcePrimitiveId),
                    Evidence = candidate.Evidence
                        .Concat(classification.Evidence)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };
            }

            return candidate;
        }

        if (classification.Category == ObjectCategory.Unknown)
        {
            return candidate;
        }

        return candidate with
        {
            Kind = KindFor(classification.Category),
            Category = classification.Category,
            DetectedTag = Clean(classification.DetectedTag) ?? candidate.DetectedTag,
            DetectedTagSourcePrimitiveId = Clean(classification.DetectedTag) is null
                ? candidate.DetectedTagSourcePrimitiveId
                : classification.DetectedTagSourcePrimitiveId,
            Confidence = new Confidence(Math.Clamp(
                Math.Max(candidate.Confidence.Value, 0.58) + classification.Score,
                0.35,
                0.82)),
            Evidence = candidate.Evidence
                .Concat(classification.Evidence)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static bool IsRoomLabelNearbyText(ObjectNearbyText text, ScanContext context) =>
        context.Rooms
            .Where(room => room.PageNumber == text.PageNumber)
            .SelectMany(room => room.LabelSourcePrimitiveIds)
            .Any(sourceId => string.Equals(sourceId, text.SourcePrimitiveId, StringComparison.Ordinal));

    private static ObjectClassification ClassifyNearbyText(IReadOnlyList<ObjectNearbyText> nearby)
    {
        foreach (var item in nearby.OrderBy(item => item.Distance))
        {
            var classification = ClassifyText(
                item.Text,
                0.18,
                hint => $"nearby text '{item.Text}' matches '{hint}'");
            if (classification.Category != ObjectCategory.Unknown)
            {
                return classification with
                {
                    DetectedTagSourcePrimitiveId = classification.DetectedTag is null
                        ? null
                        : item.SourcePrimitiveId
                };
            }
        }

        return new ObjectClassification(ObjectCategory.Unknown, 0, Array.Empty<string>());
    }

    private static IEnumerable<ObjectNearbyText> NearbyText(
        PlanPage page,
        ObjectCandidate candidate,
        ScanContext context,
        ObjectTextSpatialIndex nearbyTextIndex)
    {
        var excluded = candidate.SourcePrimitiveIds.ToHashSet(StringComparer.Ordinal);
        var searchBounds = candidate.Bounds.Inflate(context.Options.ObjectNearbyTextSearchRadius);

        return nearbyTextIndex.Query(searchBounds, excluded)
            .Select(item =>
            {
                return new ObjectNearbyText(
                    item.Text.Text.Trim(),
                    page.Number,
                    item.Text.Bounds,
                    item.SourceId,
                    Math.Round(DistanceBetween(candidate.Bounds, item.Text.Bounds), 3));
            })
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Bounds.Top)
            .ThenBy(item => item.Bounds.Left)
            .Take(context.Options.MaxNearbyTextPerObject);
    }

    private static bool LooksLikeObjectContextText(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length is > 0 and <= 64
            && !LooksLikeAnnotationOrDimension(trimmed)
            && !trimmed.Contains("scale", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Contains("note", StringComparison.OrdinalIgnoreCase);
    }

    private static double DistanceBetween(PlanRect first, PlanRect second) =>
        first.Intersects(second)
            ? 0
            : first.Center.DistanceTo(second.Center);

    private static ObjectClassification Classify(PlanPrimitive primitive)
    {
        var haystack = string.Join(
            " ",
            primitive switch
            {
                SymbolPrimitive symbol => symbol.Name,
                TextPrimitive text => text.Text,
                _ => string.Empty
            },
            primitive.Layer,
            primitive.Source.Layer,
            primitive.Source.BlockName,
            primitive.Source.EntityType);

        return ClassifyText(
            haystack,
            0.26,
            hint => $"symbol/layer text matches '{hint}'");
    }

    private static ObjectClassification ClassifyText(
        string haystack,
        double score,
        Func<string, string> evidenceFactory)
    {
        var normalized = Normalize(haystack);
        foreach (var (category, hints) in CategoryHints)
        {
            foreach (var hint in hints)
            {
                if (ContainsTokenish(normalized, Normalize(hint)))
                {
                    return new ObjectClassification(
                        category,
                        score,
                        new[] { evidenceFactory(hint) });
                }
            }
        }

        var tagClassification = ClassifyIndustrialTagText(
            haystack,
            Math.Min(0.22, score),
            evidenceFactory);
        if (tagClassification.Category != ObjectCategory.Unknown)
        {
            return tagClassification;
        }

        return new ObjectClassification(ObjectCategory.Unknown, 0, Array.Empty<string>());
    }

    private static ObjectClassification ClassifyIndustrialTagText(
        string haystack,
        double score,
        Func<string, string> evidenceFactory)
    {
        foreach (var token in IndustrialTagTokens(haystack))
        {
            if (!TrySplitIndustrialTag(token, out var prefix, out var suffix)
                || !suffix.Any(char.IsDigit)
                || !IndustrialTagPrefixes.TryGetValue(prefix, out var category))
            {
                continue;
            }

            return new ObjectClassification(
                category,
                score,
                new[] { evidenceFactory($"industrial tag {token} ({prefix})") },
                token,
                null);
        }

        return new ObjectClassification(ObjectCategory.Unknown, 0, Array.Empty<string>(), null, null);
    }

    private static IEnumerable<string> IndustrialTagTokens(string value)
    {
        var normalized = value.ToUpperInvariant();
        foreach (Match match in Regex.Matches(
            normalized,
            @"\b[A-Z]{1,5}[-_][A-Z0-9]{1,8}\b",
            RegexOptions.CultureInvariant))
        {
            var token = match.Value.Replace('_', '-');
            if (IsIgnoredIndustrialTagToken(token))
            {
                continue;
            }

            yield return token;
        }

        foreach (Match match in Regex.Matches(
            normalized,
            @"\b[A-Z]{1,5}[0-9][A-Z0-9]{0,7}\b",
            RegexOptions.CultureInvariant))
        {
            var token = match.Value;
            if (IsIgnoredIndustrialTagToken(token))
            {
                continue;
            }

            yield return token;
        }
    }

    private static bool TrySplitIndustrialTag(string token, out string prefix, out string suffix)
    {
        prefix = string.Empty;
        suffix = string.Empty;

        var separatorIndex = token.IndexOf('-', StringComparison.Ordinal);
        if (separatorIndex > 0 && separatorIndex < token.Length - 1)
        {
            prefix = token[..separatorIndex];
            suffix = token[(separatorIndex + 1)..];
            return prefix.All(char.IsLetter);
        }

        var prefixLength = 0;
        while (prefixLength < token.Length && char.IsLetter(token[prefixLength]))
        {
            prefixLength++;
        }

        if (prefixLength <= 0 || prefixLength >= token.Length)
        {
            return false;
        }

        prefix = token[..prefixLength];
        suffix = token[prefixLength..];
        return prefix.All(char.IsLetter)
            && suffix.All(char.IsLetterOrDigit)
            && (prefix.Length > 1 || suffix.Count(char.IsDigit) >= 2);
    }

    private static bool IsIgnoredIndustrialTagToken(string token) =>
        token.StartsWith("MECH", StringComparison.Ordinal)
        || token.StartsWith("ROOM", StringComparison.Ordinal)
        || token.StartsWith("AREA", StringComparison.Ordinal)
        || token.StartsWith("REV", StringComparison.Ordinal);

    private static ObjectCandidateKind KindFor(ObjectCategory category) =>
        category switch
        {
            ObjectCategory.Fixture or ObjectCategory.PlumbingFixture => ObjectCandidateKind.Fixture,
            ObjectCategory.Furniture => ObjectCandidateKind.Furniture,
            ObjectCategory.Vehicle => ObjectCandidateKind.Vehicle,
            ObjectCategory.Stair => ObjectCandidateKind.Stair,
            _ => ObjectCandidateKind.Symbol
        };

    private static RoomRegion? FindContainingRoom(int pageNumber, PlanPoint point, ScanContext context) =>
        context.Rooms
            .Where(room => room.PageNumber == pageNumber)
            .Where(room => RoomContains(room, point))
            .OrderBy(room => room.DrawingArea)
            .FirstOrDefault();

    private static bool RoomContains(RoomRegion room, PlanPoint point)
    {
        if (room.Boundary.Count >= 3)
        {
            return PointInPolygon(point, room.Boundary);
        }

        return room.Bounds.Contains(point);
    }

    private static bool PointInPolygon(PlanPoint point, IReadOnlyList<PlanPoint> polygon)
    {
        var inside = false;
        for (int index = 0, previous = polygon.Count - 1; index < polygon.Count; previous = index++)
        {
            var currentPoint = polygon[index];
            var previousPoint = polygon[previous];
            var intersects = ((currentPoint.Y > point.Y) != (previousPoint.Y > point.Y))
                && (point.X < ((previousPoint.X - currentPoint.X) * (point.Y - currentPoint.Y) / ((previousPoint.Y - currentPoint.Y) == 0 ? double.Epsilon : previousPoint.Y - currentPoint.Y)) + currentPoint.X);

            if (intersects)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool LooksLikeAnnotationOrDimension(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 64)
        {
            return true;
        }

        return trimmed.Any(char.IsDigit)
            && (trimmed.Contains('\'')
                || trimmed.Contains('"')
                || trimmed.Contains("mm", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("cm", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains(" m", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains('x')
                || trimmed.Contains('X')
                || trimmed.Contains(':'));
    }

    private static bool ContainsTokenish(string text, string hint)
    {
        if (hint.Length == 0)
        {
            return false;
        }

        if (text.Equals(hint, StringComparison.Ordinal))
        {
            return true;
        }

        var tokens = text
            .Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 0)
            .ToArray();
        return tokens.Any(token => token.Equals(hint, StringComparison.Ordinal)
            || (hint.Length >= 3 && token.StartsWith(hint, StringComparison.Ordinal)));
    }

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant().Replace('\\', '-').Replace('/', '-').Replace('.', '-').Replace(' ', '-');

    private static double PrimitiveLength(PlanPrimitive primitive) =>
        primitive switch
        {
            LinePrimitive line => line.Segment.Length,
            RectanglePrimitive rectangle => rectangle.Rectangle.IsEmpty ? 0 : (rectangle.Rectangle.Width * 2) + (rectangle.Rectangle.Height * 2),
            PolylinePrimitive polyline => PolylineLength(polyline),
            ArcPrimitive arc => Math.Abs(arc.SweepAngleRadians) * arc.Radius,
            _ => 0
        };

    private static double PolylineLength(PolylinePrimitive polyline)
    {
        if (polyline.Points.Count < 2)
        {
            return 0;
        }

        var length = 0.0;
        for (var index = 1; index < polyline.Points.Count; index++)
        {
            length += polyline.Points[index - 1].DistanceTo(polyline.Points[index]);
        }

        if (polyline.Closed)
        {
            length += polyline.Points[^1].DistanceTo(polyline.Points[0]);
        }

        return length;
    }

    private static LayerSummary? LayerSummaryFor(PlanPrimitive primitive, ScanContext context)
    {
        var layerName = LayerNameFor(primitive);
        var sourceFormat = Clean(primitive.Source.SourceFormat);
        return context.LayerAnalysis.Find(layerName, sourceFormat)
            ?? context.LayerAnalysis.Find(layerName);
    }

    private static string LayerNameFor(PlanPrimitive primitive) =>
        Clean(primitive.Source.Layer)
        ?? Clean(primitive.Layer)
        ?? LayerAnalyzer.UnlayeredName;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ObjectClassification(
        ObjectCategory Category,
        double Score,
        IReadOnlyList<string> Evidence,
        string? DetectedTag = null,
        string? DetectedTagSourcePrimitiveId = null);

    private sealed record ObjectTextItem(
        TextPrimitive Text,
        string SourceId);

    private sealed class ObjectTextSpatialIndex
    {
        private readonly Dictionary<Cell, List<ObjectTextItem>> _cells;
        private readonly double _cellSize;

        private ObjectTextSpatialIndex(Dictionary<Cell, List<ObjectTextItem>> cells, double cellSize)
        {
            _cells = cells;
            _cellSize = cellSize;
        }

        public static ObjectTextSpatialIndex Create(PlanPage page, ScanContext context)
        {
            var cellSize = Math.Max(4, Math.Max(1, context.Options.ObjectNearbyTextSearchRadius));
            var cells = new Dictionary<Cell, List<ObjectTextItem>>();

            for (var index = 0; index < page.Primitives.Count; index++)
            {
                if (page.Primitives[index] is not TextPrimitive text
                    || !LooksLikeObjectContextText(text.Text))
                {
                    continue;
                }

                AddToCells(
                    cells,
                    cellSize,
                    text.Bounds,
                    new ObjectTextItem(
                        text,
                        context.PrimitiveId(page.Number, index, text)));
            }

            return new ObjectTextSpatialIndex(cells, cellSize);
        }

        public IEnumerable<ObjectTextItem> Query(
            PlanRect search,
            IReadOnlySet<string> excludedSourceIds)
        {
            if (search.IsEmpty || _cells.Count == 0)
            {
                yield break;
            }

            var yielded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cell in CellsFor(search, _cellSize))
            {
                if (!_cells.TryGetValue(cell, out var bucket))
                {
                    continue;
                }

                foreach (var item in bucket)
                {
                    if (excludedSourceIds.Contains(item.SourceId)
                        || !yielded.Add(item.SourceId)
                        || !item.Text.Bounds.Intersects(search))
                    {
                        continue;
                    }

                    yield return item;
                }
            }
        }
    }

    private sealed class LineworkSpatialIndex
    {
        private readonly Dictionary<Cell, List<int>> _cells;
        private readonly IReadOnlyList<LineworkItem> _items;
        private readonly double _cellSize;

        private LineworkSpatialIndex(
            IReadOnlyList<LineworkItem> items,
            Dictionary<Cell, List<int>> cells,
            double cellSize)
        {
            _items = items;
            _cells = cells;
            _cellSize = cellSize;
        }

        public static LineworkSpatialIndex Create(
            IReadOnlyList<LineworkItem> items,
            double searchTolerance)
        {
            var cellSize = Math.Max(2, searchTolerance * 2);
            var cells = new Dictionary<Cell, List<int>>();

            for (var index = 0; index < items.Count; index++)
            {
                AddToCells(cells, cellSize, items[index].Bounds, index);
            }

            return new LineworkSpatialIndex(items, cells, cellSize);
        }

        public IEnumerable<int> Query(PlanRect search)
        {
            if (search.IsEmpty || _cells.Count == 0)
            {
                yield break;
            }

            var yielded = new HashSet<int>();
            foreach (var cell in CellsFor(search, _cellSize))
            {
                if (!_cells.TryGetValue(cell, out var bucket))
                {
                    continue;
                }

                foreach (var index in bucket)
                {
                    if (yielded.Add(index) && search.Intersects(_items[index].Bounds))
                    {
                        yield return index;
                    }
                }
            }
        }
    }

    private static void AddToCells<T>(
        IDictionary<Cell, List<T>> cells,
        double cellSize,
        PlanRect bounds,
        T item)
    {
        foreach (var cell in CellsFor(bounds, cellSize))
        {
            if (!cells.TryGetValue(cell, out var bucket))
            {
                bucket = new List<T>();
                cells[cell] = bucket;
            }

            bucket.Add(item);
        }
    }

    private static IEnumerable<Cell> CellsFor(PlanRect bounds, double cellSize)
    {
        var minX = CellCoordinate(bounds.Left, cellSize);
        var maxX = CellCoordinate(bounds.Right, cellSize);
        var minY = CellCoordinate(bounds.Top, cellSize);
        var maxY = CellCoordinate(bounds.Bottom, cellSize);

        for (var x = minX; x <= maxX; x++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                yield return new Cell(x, y);
            }
        }
    }

    private static int CellCoordinate(double value, double cellSize) =>
        (int)Math.Floor(value / cellSize);

    private readonly record struct Cell(int X, int Y);

    private sealed record LineworkItem(
        PlanPrimitive Primitive,
        string PrimitiveId,
        PlanRect Bounds,
        double Length,
        string LayerName,
        LayerCategory LayerCategory,
        Confidence LayerConfidence)
    {
        private const double StrongLayerConfidence = 0.45;

        public static LineworkItem? TryCreate(
            int pageNumber,
            int primitiveIndex,
            PlanPrimitive primitive,
            ScanContext context,
            IReadOnlySet<string> consumedPrimitiveIds,
            PlanRect mainRegionBounds)
        {
            var primitiveId = context.PrimitiveId(pageNumber, primitiveIndex, primitive);
            if (consumedPrimitiveIds.Contains(primitiveId)
                || primitive.Bounds.IsEmpty
                || !mainRegionBounds.Contains(primitive.Bounds.Center, context.Options.SheetMargin))
            {
                return null;
            }

            if (!IsLineworkPrimitive(primitive))
            {
                return null;
            }

            var length = PrimitiveLength(primitive);
            if (length < 1 || length > Math.Max(1, context.Options.MaxCompositeObjectPrimitiveLength))
            {
                return null;
            }

            var layerName = LayerNameFor(primitive);
            var layer = LayerSummaryFor(primitive, context);
            var category = layer?.LikelyCategory ?? LayerCategory.Unknown;
            var confidence = layer?.Confidence ?? Confidence.Low;
            if (IsExcludedLineworkLayer(category) && confidence.Value >= StrongLayerConfidence)
            {
                return null;
            }

            return new LineworkItem(
                primitive,
                primitiveId,
                primitive.Bounds,
                length,
                layerName,
                category,
                confidence);
        }

        private static bool IsLineworkPrimitive(PlanPrimitive primitive) =>
            primitive is LinePrimitive
                or ArcPrimitive
                or RectanglePrimitive
                or PolylinePrimitive;

        private static bool IsExcludedLineworkLayer(LayerCategory category) =>
            category is LayerCategory.Wall
                or LayerCategory.Door
                or LayerCategory.Window
                or LayerCategory.Room
                or LayerCategory.Dimension
                or LayerCategory.Text
                or LayerCategory.Grid;
    }
}
