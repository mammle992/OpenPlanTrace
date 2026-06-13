namespace OpenPlanTrace;

internal sealed class VisualAiClassificationStage : IPipelineStage
{
    public string Name => "visual-ai";

    public async ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        if (!context.Options.EnableVisualAiClassification)
        {
            return;
        }

        var canClassify = context.Options.VisualAiClassifier is not null;
        var canExportCrops = context.Options.VisualAiCropSink is not null;
        if (!canClassify && !canExportCrops)
        {
            context.AddDiagnostic(
                "visual_ai.classifier_missing",
                DiagnosticSeverity.Warning,
                Name,
                "Kvemo was requested, but no model-backed classifier or crop export sink was configured.",
                confidence: Confidence.None,
                scope: DiagnosticScope.Document,
                properties: new Dictionary<string, string>
                {
                    ["requiredOption"] = $"{nameof(ScannerOptions.VisualAiClassifier)} or {nameof(ScannerOptions.VisualAiCropSink)}"
                });
            return;
        }

        if (!canClassify && canExportCrops)
        {
            context.AddDiagnostic(
                "kvemo.crop_export_only",
                DiagnosticSeverity.Info,
                Name,
                "Kvemo is running in crop-export mode without a model-backed classifier.",
                confidence: Confidence.Medium,
                scope: DiagnosticScope.Document);
        }

        if (context.ObjectCandidates.Count == 0)
        {
            context.AddDiagnostic(
                "visual_ai.no_object_candidates",
                DiagnosticSeverity.Info,
                Name,
                "Kvemo was requested, but no deterministic object candidates were available.",
                confidence: Confidence.Low,
                scope: DiagnosticScope.Document);
            return;
        }

        var cropProvider = context.Options.VisualAiCropProvider
            ?? new PrimitiveVectorVisualAiCropProvider();
        var maxCrops = Math.Max(0, context.Options.MaxVisualAiCropsPerScan);
        if (maxCrops == 0)
        {
            context.AddDiagnostic(
                "visual_ai.crop_limit_zero",
                DiagnosticSeverity.Warning,
                Name,
                "Visual AI classification was requested, but the configured crop limit is zero.",
                confidence: Confidence.Low,
                scope: DiagnosticScope.Document);
            return;
        }

        var candidatesById = context.ObjectCandidates
            .Select((candidate, index) => new { candidate, index })
            .ToDictionary(item => item.candidate.Id, item => item, StringComparer.Ordinal);
        var groupedCandidateIds = new HashSet<string>(StringComparer.Ordinal);
        var attempted = 0;
        var classified = 0;
        var cropMisses = 0;
        var modelMisses = 0;
        var belowThreshold = 0;
        var cropOnly = 0;

        foreach (var groupIndex in GroupClassificationOrder(context))
        {
            if (attempted >= maxCrops)
            {
                break;
            }

            var group = context.ObjectGroups[groupIndex];
            foreach (var candidateId in group.CandidateIds)
            {
                groupedCandidateIds.Add(candidateId);
            }

            attempted++;
            var classification = await ClassifyGroupAsync(context, group, cropProvider, cancellationToken)
                .ConfigureAwait(false);
            if (classification.Outcome == VisualAiStageOutcome.CropMissing)
            {
                cropMisses++;
                continue;
            }

            if (classification.Outcome == VisualAiStageOutcome.ModelMissing)
            {
                modelMisses++;
                continue;
            }

            if (classification.Outcome == VisualAiStageOutcome.Cropped)
            {
                cropOnly++;
                continue;
            }

            if (classification.Classification is null)
            {
                continue;
            }

            var accepted = classification.Classification.Confidence >= context.Options.MinVisualAiConfidence;
            if (!accepted)
            {
                belowThreshold++;
            }

            context.ObjectGroups[groupIndex] = ApplyToGroup(group, classification.Classification, accepted);
            foreach (var candidateId in group.CandidateIds)
            {
                if (!candidatesById.TryGetValue(candidateId, out var item))
                {
                    continue;
                }

                context.ObjectCandidates[item.index] = ApplyToCandidate(
                    item.candidate,
                    classification.Classification,
                    accepted,
                    $"visual AI group classification applied from {group.Id}");
            }

            classified++;
        }

        foreach (var candidateIndex in CandidateClassificationOrder(context, groupedCandidateIds))
        {
            if (attempted >= maxCrops)
            {
                break;
            }

            var candidate = context.ObjectCandidates[candidateIndex];
            attempted++;
            var classification = await ClassifyCandidateAsync(context, candidate, cropProvider, cancellationToken)
                .ConfigureAwait(false);
            if (classification.Outcome == VisualAiStageOutcome.CropMissing)
            {
                cropMisses++;
                continue;
            }

            if (classification.Outcome == VisualAiStageOutcome.ModelMissing)
            {
                modelMisses++;
                continue;
            }

            if (classification.Outcome == VisualAiStageOutcome.Cropped)
            {
                cropOnly++;
                continue;
            }

            if (classification.Classification is null)
            {
                continue;
            }

            var accepted = classification.Classification.Confidence >= context.Options.MinVisualAiConfidence;
            if (!accepted)
            {
                belowThreshold++;
            }

            context.ObjectCandidates[candidateIndex] = ApplyToCandidate(
                candidate,
                classification.Classification,
                accepted,
                "visual AI candidate classification applied");
            classified++;
        }

        AddSummaryDiagnostic(
            context,
            attempted,
            classified,
            cropMisses,
            modelMisses,
            belowThreshold,
            cropOnly,
            maxCrops);
    }

    private static IEnumerable<int> GroupClassificationOrder(ScanContext context) =>
        context.ObjectGroups
            .Select((group, index) => new { group, index })
            .OrderByDescending(item => item.group.RequiresReview)
            .ThenByDescending(item => item.group.Count)
            .ThenBy(item => item.group.RepresentativeBounds.Top)
            .ThenBy(item => item.group.RepresentativeBounds.Left)
            .Select(item => item.index);

    private static IEnumerable<int> CandidateClassificationOrder(
        ScanContext context,
        IReadOnlySet<string> groupedCandidateIds) =>
        context.ObjectCandidates
            .Select((candidate, index) => new { candidate, index })
            .Where(item => item.candidate.Kind != ObjectCandidateKind.TextLabel)
            .Where(item => !groupedCandidateIds.Contains(item.candidate.Id))
            .OrderByDescending(item => item.candidate.Category is ObjectCategory.Unknown or ObjectCategory.GenericSymbol)
            .ThenBy(item => item.candidate.PageNumber)
            .ThenBy(item => item.candidate.Bounds.Top)
            .ThenBy(item => item.candidate.Bounds.Left)
            .Select(item => item.index);

    private async ValueTask<VisualAiStageResult> ClassifyGroupAsync(
        ScanContext context,
        ObjectCandidateGroup group,
        IVisualAiCropProvider cropProvider,
        CancellationToken cancellationToken)
    {
        var pageNumber = group.PageNumbers.FirstOrDefault();
        var cropBounds = CropBounds(group.RepresentativeBounds, context.Options.VisualAiCropPadding, context, pageNumber);
        var crop = await cropProvider.GetCropAsync(
                context.Document,
                new VisualAiCropRequest(
                    group.Id,
                    pageNumber,
                    group.RepresentativeBounds,
                    cropBounds,
                    group.SourcePrimitiveIds),
                cancellationToken)
            .ConfigureAwait(false);
        if (crop is null)
        {
            return VisualAiStageResult.CropMissing();
        }

        var nearbyText = group.NearbyText
            .Select(text => text.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sourceProvenance = SourceProvenanceForGroup(context, group);
        if (context.Options.VisualAiClassifier is null)
        {
            await SaveCropAsync(
                    context,
                    group.Id,
                    "object-group",
                    group.Signature,
                    pageNumber,
                    group.RepresentativeBounds,
                    cropBounds,
                    group.Kind,
                    group.Category,
                    sourceProvenance.SourceKind,
                    sourceProvenance.SourceWallComponentId,
                    sourceProvenance.SourceWallComponentKind,
                    group.Confidence.Value,
                    group.Label,
                    group.SymbolName,
                    group.DetectedTags,
                    nearbyText,
                    group.SourcePrimitiveIds,
                    group.Evidence,
                    crop,
                    null,
                    cancellationToken,
                    sourceKindCounts: sourceProvenance.SourceKindCounts,
                    sourceWallComponentIds: sourceProvenance.SourceWallComponentIds,
                    sourceWallComponentKindCounts: sourceProvenance.SourceWallComponentKindCounts)
                .ConfigureAwait(false);
            return VisualAiStageResult.Cropped();
        }

        var result = await context.Options.VisualAiClassifier!.ClassifyAsync(
                new VisualAiClassificationRequest(
                    group.Id,
                    "object-group",
                    pageNumber,
                    group.RepresentativeBounds,
                    cropBounds,
                    group.Kind,
                    group.Category,
                    group.Label,
                    group.SymbolName,
                    nearbyText,
                    group.SourcePrimitiveIds,
                    crop),
                cancellationToken)
            .ConfigureAwait(false);

        var classification = result is null ? null : ToClassification(result, pageNumber, cropBounds, crop.SourceId);
        await SaveCropAsync(
                context,
                group.Id,
                "object-group",
                group.Signature,
                pageNumber,
                group.RepresentativeBounds,
                cropBounds,
                group.Kind,
                group.Category,
                sourceProvenance.SourceKind,
                sourceProvenance.SourceWallComponentId,
                sourceProvenance.SourceWallComponentKind,
                group.Confidence.Value,
                group.Label,
                group.SymbolName,
                group.DetectedTags,
                nearbyText,
                group.SourcePrimitiveIds,
                group.Evidence,
                crop,
                classification,
                cancellationToken,
                sourceKindCounts: sourceProvenance.SourceKindCounts,
                sourceWallComponentIds: sourceProvenance.SourceWallComponentIds,
                sourceWallComponentKindCounts: sourceProvenance.SourceWallComponentKindCounts)
            .ConfigureAwait(false);

        return classification is null
            ? VisualAiStageResult.ModelMissing()
            : VisualAiStageResult.Classified(classification);
    }

    private async ValueTask<VisualAiStageResult> ClassifyCandidateAsync(
        ScanContext context,
        ObjectCandidate candidate,
        IVisualAiCropProvider cropProvider,
        CancellationToken cancellationToken)
    {
        var cropBounds = CropBounds(candidate.Bounds, context.Options.VisualAiCropPadding, context, candidate.PageNumber);
        var crop = await cropProvider.GetCropAsync(
                context.Document,
                new VisualAiCropRequest(
                    candidate.Id,
                    candidate.PageNumber,
                    candidate.Bounds,
                    cropBounds,
                    candidate.SourcePrimitiveIds),
                cancellationToken)
            .ConfigureAwait(false);
        if (crop is null)
        {
            return VisualAiStageResult.CropMissing();
        }

        var nearbyText = candidate.NearbyText
            .Select(text => text.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (context.Options.VisualAiClassifier is null)
        {
            await SaveCropAsync(
                    context,
                    candidate.Id,
                    "object",
                    null,
                    candidate.PageNumber,
                    candidate.Bounds,
                    cropBounds,
                    candidate.Kind,
                    candidate.Category,
                    candidate.SourceKind,
                    candidate.SourceWallComponentId,
                    candidate.SourceWallComponentKind,
                    candidate.Confidence.Value,
                    candidate.Label,
                    candidate.SymbolName,
                    TagsFor(candidate),
                    nearbyText,
                    candidate.SourcePrimitiveIds,
                    candidate.Evidence,
                    crop,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            return VisualAiStageResult.Cropped();
        }

        var result = await context.Options.VisualAiClassifier!.ClassifyAsync(
                new VisualAiClassificationRequest(
                    candidate.Id,
                    "object",
                    candidate.PageNumber,
                    candidate.Bounds,
                    cropBounds,
                    candidate.Kind,
                    candidate.Category,
                    candidate.Label,
                    candidate.SymbolName,
                    nearbyText,
                    candidate.SourcePrimitiveIds,
                    crop),
                cancellationToken)
            .ConfigureAwait(false);

        var classification = result is null ? null : ToClassification(result, candidate.PageNumber, cropBounds, crop.SourceId);
        await SaveCropAsync(
                context,
                candidate.Id,
                "object",
                null,
                candidate.PageNumber,
                candidate.Bounds,
                cropBounds,
                candidate.Kind,
                candidate.Category,
                candidate.SourceKind,
                candidate.SourceWallComponentId,
                candidate.SourceWallComponentKind,
                candidate.Confidence.Value,
                candidate.Label,
                candidate.SymbolName,
                TagsFor(candidate),
                nearbyText,
                candidate.SourcePrimitiveIds,
                candidate.Evidence,
                crop,
                classification,
                cancellationToken)
            .ConfigureAwait(false);

        return classification is null
            ? VisualAiStageResult.ModelMissing()
            : VisualAiStageResult.Classified(classification);
    }

    private static VisualAiSourceProvenance SourceProvenanceForGroup(
        ScanContext context,
        ObjectCandidateGroup group)
    {
        var candidateIds = group.CandidateIds.ToHashSet(StringComparer.Ordinal);
        var candidates = context.ObjectCandidates
            .Where(candidate => candidateIds.Contains(candidate.Id))
            .ToArray();
        if (candidates.Length == 0)
        {
            return VisualAiSourceProvenance.Unknown;
        }

        var sourceKinds = candidates
            .Select(candidate => candidate.SourceKind)
            .Distinct()
            .ToArray();
        var sourceKind = sourceKinds.Length == 1
            ? sourceKinds[0]
            : ObjectCandidateSourceKind.Unknown;

        var wallComponentIds = candidates
            .Select(candidate => candidate.SourceWallComponentId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var wallComponentKinds = candidates
            .Select(candidate => candidate.SourceWallComponentKind)
            .Where(kind => kind.HasValue)
            .Select(kind => kind!.Value)
            .Distinct()
            .ToArray();

        return new VisualAiSourceProvenance(
            sourceKind,
            wallComponentIds.Length == 1 ? wallComponentIds[0] : null,
            wallComponentKinds.Length == 1 ? wallComponentKinds[0] : null,
            CountValues(candidates.Select(candidate => candidate.SourceKind.ToString())),
            wallComponentIds,
            CountValues(wallComponentKinds.Select(kind => kind.ToString())));
    }

    private static async ValueTask SaveCropAsync(
        ScanContext context,
        string detectionId,
        string detectionKind,
        string? groupSignature,
        int pageNumber,
        PlanRect bounds,
        PlanRect cropBounds,
        ObjectCandidateKind candidateKind,
        ObjectCategory category,
        ObjectCandidateSourceKind sourceKind,
        string? sourceWallComponentId,
        WallGraphComponentKind? sourceWallComponentKind,
        double deterministicConfidence,
        string? label,
        string? symbolName,
        IReadOnlyList<string> detectedTags,
        IReadOnlyList<string> nearbyText,
        IReadOnlyList<string> sourcePrimitiveIds,
        IReadOnlyList<string> evidence,
        VisualAiImage crop,
        VisualAiClassification? classification,
        CancellationToken cancellationToken,
        IReadOnlyList<VisualAiProvenanceCount>? sourceKindCounts = null,
        IReadOnlyList<string>? sourceWallComponentIds = null,
        IReadOnlyList<VisualAiProvenanceCount>? sourceWallComponentKindCounts = null)
    {
        if (context.Options.VisualAiCropSink is null)
        {
            return;
        }

        await context.Options.VisualAiCropSink.SaveCropAsync(
                context.Document,
                new VisualAiCropArtifact(
                    detectionId,
                    detectionKind,
                    groupSignature,
                    pageNumber,
                    bounds,
                    cropBounds,
                    candidateKind,
                    category,
                    sourceKind,
                    sourceWallComponentId,
                    sourceWallComponentKind,
                    deterministicConfidence,
                    label,
                    symbolName,
                    detectedTags,
                    nearbyText,
                    sourcePrimitiveIds,
                    evidence,
                    crop,
                    classification)
                {
                    SourceKindCounts = sourceKindCounts ?? CountValues(new[] { sourceKind.ToString() }),
                    SourceWallComponentIds = sourceWallComponentIds ?? SingleOrEmpty(sourceWallComponentId),
                    SourceWallComponentKindCounts = sourceWallComponentKindCounts
                        ?? CountValues(sourceWallComponentKind.HasValue
                            ? new[] { sourceWallComponentKind.Value.ToString() }
                            : Array.Empty<string>())
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<string> TagsFor(ObjectCandidate candidate) =>
        string.IsNullOrWhiteSpace(candidate.DetectedTag)
            ? Array.Empty<string>()
            : new[] { candidate.DetectedTag.Trim() };

    private static IReadOnlyList<VisualAiProvenanceCount> CountValues(IEnumerable<string?> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new VisualAiProvenanceCount(group.Key, group.Count()))
            .OrderByDescending(count => count.Count)
            .ThenBy(count => count.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> SingleOrEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : new[] { value.Trim() };

    private sealed record VisualAiSourceProvenance(
        ObjectCandidateSourceKind SourceKind,
        string? SourceWallComponentId,
        WallGraphComponentKind? SourceWallComponentKind,
        IReadOnlyList<VisualAiProvenanceCount> SourceKindCounts,
        IReadOnlyList<string> SourceWallComponentIds,
        IReadOnlyList<VisualAiProvenanceCount> SourceWallComponentKindCounts)
    {
        public static VisualAiSourceProvenance Unknown { get; } = new(
            ObjectCandidateSourceKind.Unknown,
            null,
            null,
            CountValues(new[] { ObjectCandidateSourceKind.Unknown.ToString() }),
            Array.Empty<string>(),
            Array.Empty<VisualAiProvenanceCount>());
    }

    private static VisualAiClassification ToClassification(
        VisualAiClassificationResult result,
        int pageNumber,
        PlanRect cropBounds,
        string cropSourceId)
    {
        var prediction = NormalizePrediction(result.Prediction);
        return new VisualAiClassification(
            prediction.Label,
            prediction.Category,
            Math.Clamp(prediction.Confidence, 0, 1),
            Clean(result.ModelName) ?? "unknown-model",
            Clean(result.ModelVersion) ?? "unknown-version",
            Clean(result.InferenceEngine) ?? "unknown-engine",
            pageNumber,
            cropBounds,
            Clean(cropSourceId) ?? string.Empty,
            result.Alternatives.Select(NormalizePrediction).ToArray(),
            result.Evidence.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static VisualAiClassificationCandidate NormalizePrediction(VisualAiClassificationCandidate prediction)
    {
        var label = Clean(prediction.Label) ?? "unknown";
        var category = prediction.Category == ObjectCategory.Unknown
            ? VisualAiCategoryMapper.MapLabel(label)
            : prediction.Category;
        return prediction with
        {
            Label = label,
            Category = category,
            Confidence = Math.Clamp(prediction.Confidence, 0, 1)
        };
    }

    private static ObjectCandidateGroup ApplyToGroup(
        ObjectCandidateGroup group,
        VisualAiClassification classification,
        bool accepted)
    {
        var evidence = group.Evidence
            .Concat(new[]
            {
                accepted
                    ? $"Kvemo visual AI classified representative crop as '{classification.Label}' ({classification.Confidence:0.###}) using {classification.ModelName} {classification.ModelVersion}"
                    : $"Kvemo visual AI low-confidence representative classification '{classification.Label}' ({classification.Confidence:0.###}) retained for review"
            })
            .Concat(classification.Evidence.Select(item => $"Kvemo visual AI evidence: {item}"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (!accepted)
        {
            return group with
            {
                VisualAi = classification,
                Evidence = evidence,
                RequiresReview = true
            };
        }

        var category = classification.Category == ObjectCategory.Unknown
            ? group.Category
            : classification.Category;
        return group with
        {
            VisualAi = classification,
            Label = classification.Label,
            Category = category,
            Kind = VisualAiCategoryMapper.KindFor(category),
            Confidence = new Confidence(Math.Clamp(Math.Max(group.Confidence.Value, classification.Confidence), 0.35, 0.97)),
            Evidence = evidence,
            RequiresReview = group.RequiresReview && classification.Confidence < 0.75
        };
    }

    private static ObjectCandidate ApplyToCandidate(
        ObjectCandidate candidate,
        VisualAiClassification classification,
        bool accepted,
        string evidenceLine)
    {
        var evidence = candidate.Evidence
            .Concat(new[]
            {
                accepted
                    ? $"Kvemo {evidenceLine}: '{classification.Label}' ({classification.Confidence:0.###})"
                    : $"Kvemo visual AI low-confidence classification retained for review: '{classification.Label}' ({classification.Confidence:0.###})"
            })
            .Concat(classification.Evidence.Select(item => $"Kvemo visual AI evidence: {item}"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (!accepted)
        {
            return candidate with
            {
                VisualAi = classification,
                Evidence = evidence
            };
        }

        var category = classification.Category == ObjectCategory.Unknown
            ? candidate.Category
            : classification.Category;
        return candidate with
        {
            VisualAi = classification,
            Label = classification.Label,
            Category = category,
            Kind = VisualAiCategoryMapper.KindFor(category),
            Confidence = new Confidence(Math.Clamp(Math.Max(candidate.Confidence.Value, classification.Confidence), 0.35, 0.97)),
            Evidence = evidence
        };
    }

    private static PlanRect CropBounds(
        PlanRect bounds,
        double padding,
        ScanContext context,
        int pageNumber)
    {
        var page = context.Document.Pages.FirstOrDefault(page => page.Number == pageNumber);
        var adaptivePadding = AdaptiveCropPadding(bounds, padding);
        var crop = bounds.Inflate(adaptivePadding);
        if (page is null)
        {
            return crop;
        }

        return crop.ClampTo(new PlanRect(0, 0, page.Size.Width, page.Size.Height));
    }

    private static double AdaptiveCropPadding(PlanRect bounds, double configuredPadding)
    {
        var maximum = Math.Max(0, configuredPadding);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return maximum;
        }

        var major = Math.Max(bounds.Width, bounds.Height);
        var minor = Math.Min(bounds.Width, bounds.Height);
        var relativePadding = Math.Max(2, Math.Min(major * 0.6, minor * 1.1));
        return Math.Min(maximum, relativePadding);
    }

    private static void AddSummaryDiagnostic(
        ScanContext context,
        int attempted,
        int classified,
        int cropMisses,
        int modelMisses,
        int belowThreshold,
        int cropOnly,
        int maxCrops)
    {
        var cropExportOnly = classified == 0 && cropOnly > 0;
        var severity = classified == 0 && !cropExportOnly ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info;
        context.AddDiagnostic(
            classified == 0
                ? cropExportOnly ? "kvemo.crops.exported" : "visual_ai.no_classifications"
                : "visual_ai.classifications.detected",
            severity,
            "visual-ai",
            classified == 0
                ? cropExportOnly
                    ? $"Kvemo exported {cropOnly} object crop(s) from {attempted} attempted crop(s)."
                    : "Kvemo was configured but produced no usable classifications."
                : $"Kvemo classified {classified} object crop(s) from {attempted} attempted crop(s).",
            confidence: classified == 0 && !cropExportOnly ? Confidence.Low : Confidence.Medium,
            scope: DiagnosticScope.Detection,
            properties: new Dictionary<string, string>
            {
                ["attemptedCropCount"] = attempted.ToString(),
                ["classifiedCropCount"] = classified.ToString(),
                ["cropOnlyCount"] = cropOnly.ToString(),
                ["cropMissingCount"] = cropMisses.ToString(),
                ["modelNoResultCount"] = modelMisses.ToString(),
                ["belowThresholdCount"] = belowThreshold.ToString(),
                ["maxVisualAiCropsPerScan"] = maxCrops.ToString(),
                ["minVisualAiConfidence"] = context.Options.MinVisualAiConfidence.ToString("0.###")
            });
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private enum VisualAiStageOutcome
    {
        Classified,
        Cropped,
        CropMissing,
        ModelMissing
    }

    private sealed record VisualAiStageResult(
        VisualAiStageOutcome Outcome,
        VisualAiClassification? Classification)
    {
        public static VisualAiStageResult Classified(VisualAiClassification classification) =>
            new(VisualAiStageOutcome.Classified, classification);

        public static VisualAiStageResult Cropped() =>
            new(VisualAiStageOutcome.Cropped, null);

        public static VisualAiStageResult CropMissing() =>
            new(VisualAiStageOutcome.CropMissing, null);

        public static VisualAiStageResult ModelMissing() =>
            new(VisualAiStageOutcome.ModelMissing, null);
    }
}
