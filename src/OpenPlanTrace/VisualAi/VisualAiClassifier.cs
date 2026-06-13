namespace OpenPlanTrace;

public sealed record VisualAiClassificationRequest(
    string DetectionId,
    string DetectionKind,
    int PageNumber,
    PlanRect Bounds,
    PlanRect CropBounds,
    ObjectCandidateKind CandidateKind,
    ObjectCategory DeterministicCategory,
    string? DeterministicLabel,
    string? SymbolName,
    IReadOnlyList<string> NearbyText,
    IReadOnlyList<string> SourcePrimitiveIds,
    VisualAiImage Crop);

public sealed record VisualAiClassificationCandidate(
    string Label,
    ObjectCategory Category,
    double Confidence,
    IReadOnlyDictionary<string, string> Evidence);

public sealed record VisualAiClassificationResult(
    string ModelName,
    string ModelVersion,
    string InferenceEngine,
    VisualAiClassificationCandidate Prediction,
    IReadOnlyList<VisualAiClassificationCandidate> Alternatives,
    IReadOnlyList<string> Evidence);

public interface IVisualAiObjectClassifier
{
    ValueTask<VisualAiClassificationResult?> ClassifyAsync(
        VisualAiClassificationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record VisualAiClassification(
    string Label,
    ObjectCategory Category,
    double Confidence,
    string ModelName,
    string ModelVersion,
    string InferenceEngine,
    int PageNumber,
    PlanRect CropBounds,
    string CropSourceId,
    IReadOnlyList<VisualAiClassificationCandidate> Alternatives,
    IReadOnlyList<string> Evidence);
