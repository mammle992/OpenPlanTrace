namespace OpenPlanTrace;

public sealed record VisualAiImage(
    int Width,
    int Height,
    int Channels,
    IReadOnlyList<byte> Pixels,
    string ColorSpace,
    string SourceId)
{
    public static VisualAiImage Rgb(
        int width,
        int height,
        IReadOnlyList<byte> pixels,
        string sourceId = "") =>
        new(width, height, 3, pixels, "RGB", sourceId);
}

public sealed record VisualAiCropRequest(
    string DetectionId,
    int PageNumber,
    PlanRect Bounds,
    PlanRect CropBounds,
    IReadOnlyList<string> SourcePrimitiveIds);

public interface IVisualAiCropProvider
{
    ValueTask<VisualAiImage?> GetCropAsync(
        PlanDocument document,
        VisualAiCropRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record VisualAiCropArtifact(
    string DetectionId,
    string DetectionKind,
    string? GroupSignature,
    int PageNumber,
    PlanRect Bounds,
    PlanRect CropBounds,
    ObjectCandidateKind CandidateKind,
    ObjectCategory Category,
    ObjectCandidateSourceKind SourceKind,
    string? SourceWallComponentId,
    WallGraphComponentKind? SourceWallComponentKind,
    double DeterministicConfidence,
    string? Label,
    string? SymbolName,
    IReadOnlyList<string> DetectedTags,
    IReadOnlyList<string> NearbyText,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence,
    VisualAiImage Image,
    VisualAiClassification? Classification)
{
    public IReadOnlyList<VisualAiProvenanceCount> SourceKindCounts { get; init; } = Array.Empty<VisualAiProvenanceCount>();

    public IReadOnlyList<string> SourceWallComponentIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<VisualAiProvenanceCount> SourceWallComponentKindCounts { get; init; } = Array.Empty<VisualAiProvenanceCount>();
}

public sealed record VisualAiProvenanceCount(string Value, int Count);

public interface IVisualAiCropSink
{
    ValueTask SaveCropAsync(
        PlanDocument document,
        VisualAiCropArtifact artifact,
        CancellationToken cancellationToken = default);
}
