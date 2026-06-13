using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenPlanTrace;

public sealed class DirectoryVisualAiCropSink : IVisualAiCropSink, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;
    private readonly string _manifestPath;
    private int _sequence;
    private bool _disposed;

    public DirectoryVisualAiCropSink(string directory, string manifestFileName = "kvemo-crops.jsonl")
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Kvemo crop directory is required.", nameof(directory));
        }

        if (string.IsNullOrWhiteSpace(manifestFileName))
        {
            throw new ArgumentException("Kvemo crop manifest file name is required.", nameof(manifestFileName));
        }

        _directory = Path.GetFullPath(directory);
        _manifestPath = Path.Combine(_directory, manifestFileName);
    }

    public async ValueTask SaveCropAsync(
        PlanDocument document,
        VisualAiCropArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();

        if (artifact.Image.Channels != 3 || artifact.Image.Width <= 0 || artifact.Image.Height <= 0)
        {
            return;
        }

        Directory.CreateDirectory(_directory);

        var sequence = Interlocked.Increment(ref _sequence);
        var fileName = string.Join(
            "-",
            new[]
            {
                "kvemo",
                sequence.ToString("000000"),
                SafeFilePart(document.Id),
                $"p{artifact.PageNumber}",
                SafeFilePart(artifact.DetectionKind),
                SafeFilePart(artifact.DetectionId)
            }.Where(part => !string.IsNullOrWhiteSpace(part))) + ".png";
        var imagePath = Path.Combine(_directory, fileName);
        var imageBytes = VisualAiPngWriter.WriteRgbPng(artifact.Image);
        await File.WriteAllBytesAsync(imagePath, imageBytes, cancellationToken).ConfigureAwait(false);

        var entry = VisualAiCropManifestEntry.From(
            document,
            artifact,
            imagePath,
            Path.GetRelativePath(_directory, imagePath));
        var json = JsonSerializer.Serialize(entry, JsonOptions);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_manifestPath, json + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DirectoryVisualAiCropSink));
        }
    }

    private static string SafeFilePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(character =>
            invalid.Contains(character) || char.IsWhiteSpace(character) ? '_' : character).ToArray();
        var result = new string(chars).Trim('_');
        return result.Length <= 80 ? result : result[..80];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Dispose();
        _disposed = true;
    }
}

public sealed record VisualAiCropManifestEntry(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    string Engine,
    string DocumentId,
    string DetectionId,
    string DetectionKind,
    string ReviewKey,
    string? GroupSignature,
    int PageNumber,
    double PageWidth,
    double PageHeight,
    string CoordinateSpace,
    string CoordinateOrigin,
    string CoordinateYAxisDirection,
    VisualAiRectManifestEntry Bounds,
    VisualAiRectManifestEntry CropBounds,
    double ObjectToCropAreaRatio,
    double CropToPageAreaRatio,
    ObjectCandidateKind CandidateKind,
    ObjectCategory Category,
    ObjectCandidateSourceKind SourceKind,
    string? SourceWallComponentId,
    WallGraphComponentKind? SourceWallComponentKind,
    IReadOnlyList<VisualAiProvenanceCount> SourceKindCounts,
    IReadOnlyList<string> SourceWallComponentIds,
    IReadOnlyList<VisualAiProvenanceCount> SourceWallComponentKindCounts,
    double DeterministicConfidence,
    string? Label,
    string? SymbolName,
    IReadOnlyList<string> DetectedTags,
    IReadOnlyList<string> NearbyText,
    int NearbyTextCount,
    IReadOnlyList<string> SourcePrimitiveIds,
    VisualAiSourceEvidenceManifestEntry SourceEvidence,
    IReadOnlyList<string> Evidence,
    string ReviewPriority,
    IReadOnlyList<string> ReviewReasons,
    string SuggestedTrainingUse,
    string VisualSimilarityKey,
    VisualAiImageFingerprint ImageFingerprint,
    string ImagePath,
    string ImageFileName,
    int ImageWidth,
    int ImageHeight,
    string ImageColorSpace,
    string ImageSourceId,
    VisualAiClassificationManifestEntry? Classification)
{
    public const string CurrentSchemaVersion = "openplantrace.kvemo-crops.v2";

    public static VisualAiCropManifestEntry From(
        PlanDocument document,
        VisualAiCropArtifact artifact,
        string imagePath,
        string imageFileName)
    {
        var page = document.Pages.FirstOrDefault(page => page.Number == artifact.PageNumber);
        var pageArea = page is null ? 0 : page.Size.Width * page.Size.Height;
        var objectToCropAreaRatio = Ratio(artifact.Bounds.Area, artifact.CropBounds.Area);
        var cropToPageAreaRatio = Ratio(artifact.CropBounds.Area, pageArea);
        var sourceEvidence = VisualAiSourceEvidenceManifestEntry.From(document, artifact.SourcePrimitiveIds);
        var review = VisualAiCropReviewSummary.From(artifact, sourceEvidence, objectToCropAreaRatio);
        var fingerprint = VisualAiImageFingerprint.From(artifact.Image, artifact.Bounds);
        var reviewKey = ReviewKeyFor(artifact, sourceEvidence, fingerprint);

        return new(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            "Kvemo",
            document.Id,
            artifact.DetectionId,
            artifact.DetectionKind,
            reviewKey,
            Clean(artifact.GroupSignature),
            artifact.PageNumber,
            page?.Size.Width ?? 0,
            page?.Size.Height ?? 0,
            "page",
            "top-left",
            "down",
            VisualAiRectManifestEntry.From(artifact.Bounds),
            VisualAiRectManifestEntry.From(artifact.CropBounds),
            objectToCropAreaRatio,
            cropToPageAreaRatio,
            artifact.CandidateKind,
            artifact.Category,
            artifact.SourceKind,
            artifact.SourceWallComponentId,
            artifact.SourceWallComponentKind,
            NormalizeCounts(artifact.SourceKindCounts, artifact.SourceKind.ToString()),
            artifact.SourceWallComponentIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            NormalizeCounts(
                artifact.SourceWallComponentKindCounts,
                artifact.SourceWallComponentKind?.ToString(),
                includeFallbackWhenMissing: false),
            Math.Clamp(artifact.DeterministicConfidence, 0, 1),
            artifact.Label,
            artifact.SymbolName,
            artifact.DetectedTags,
            artifact.NearbyText,
            artifact.NearbyText.Count,
            artifact.SourcePrimitiveIds,
            sourceEvidence,
            artifact.Evidence,
            review.Priority,
            review.Reasons,
            review.SuggestedTrainingUse,
            fingerprint.SimilarityKey,
            fingerprint,
            imagePath,
            imageFileName,
            artifact.Image.Width,
            artifact.Image.Height,
            artifact.Image.ColorSpace,
            artifact.Image.SourceId,
            artifact.Classification is null ? null : VisualAiClassificationManifestEntry.From(artifact.Classification));
    }

    private static double Ratio(double numerator, double denominator) =>
        denominator <= 0 ? 0 : Math.Round(Math.Clamp(numerator / denominator, 0, 1), 6);

    private static IReadOnlyList<VisualAiProvenanceCount> NormalizeCounts(
        IReadOnlyList<VisualAiProvenanceCount> counts,
        string? fallback,
        bool includeFallbackWhenMissing = true)
    {
        var normalized = counts
            .Where(count => !string.IsNullOrWhiteSpace(count.Value) && count.Count > 0)
            .GroupBy(count => count.Value.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new VisualAiProvenanceCount(group.Key, group.Sum(count => count.Count)))
            .OrderByDescending(count => count.Count)
            .ThenBy(count => count.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length > 0 || !includeFallbackWhenMissing || string.IsNullOrWhiteSpace(fallback))
        {
            return normalized;
        }

        return new[] { new VisualAiProvenanceCount(fallback.Trim(), 1) };
    }

    private static string ReviewKeyFor(
        VisualAiCropArtifact artifact,
        VisualAiSourceEvidenceManifestEntry sourceEvidence,
        VisualAiImageFingerprint fingerprint)
    {
        var groupSignature = Clean(artifact.GroupSignature);
        if (groupSignature is not null)
        {
            return groupSignature;
        }

        var symbolName = Clean(artifact.SymbolName);
        if (symbolName is not null)
        {
            var layerKey = sourceEvidence.Layers.Count == 0
                ? "none"
                : string.Join(",", sourceEvidence.Layers.Select(value => value.ToLowerInvariant()));
            return $"symbol:{symbolName.ToLowerInvariant()}|category:{artifact.Category}|kind:{artifact.CandidateKind}|layers:{layerKey}";
        }

        if (artifact.DetectedTags.Count > 0)
        {
            return $"tags:{string.Join(",", artifact.DetectedTags.Select(value => value.Trim()).Order(StringComparer.OrdinalIgnoreCase))}";
        }

        if (!string.IsNullOrWhiteSpace(fingerprint.SimilarityKey))
        {
            return $"visual:{fingerprint.SimilarityKey}";
        }

        return $"{artifact.DetectionKind}:{artifact.DetectionId}";
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record VisualAiRectManifestEntry(double X, double Y, double Width, double Height)
{
    public static VisualAiRectManifestEntry From(PlanRect rect) =>
        new(rect.X, rect.Y, rect.Width, rect.Height);
}

public sealed record VisualAiSourceEvidenceManifestEntry(
    int PrimitiveCount,
    int ResolvedPrimitiveCount,
    IReadOnlyList<string> PrimitiveIdsSample,
    IReadOnlyList<string> UnresolvedPrimitiveIdsSample,
    IReadOnlyList<string> SourceFormats,
    IReadOnlyList<string> Layers,
    IReadOnlyList<string> EntityTypes,
    IReadOnlyList<string> BlockNames,
    IReadOnlyList<string> Colors,
    IReadOnlyList<string> LineTypes,
    IReadOnlyList<string> DrawingSpaces)
{
    private const int SampleLimit = 32;

    public static VisualAiSourceEvidenceManifestEntry From(
        PlanDocument document,
        IReadOnlyList<string> sourcePrimitiveIds)
    {
        var lookup = BuildSourceLookup(document);
        var resolved = sourcePrimitiveIds
            .Select(sourceId => new
            {
                SourceId = sourceId,
                Metadata = lookup.TryGetValue(sourceId, out var metadata) ? metadata : null
            })
            .ToArray();
        var metadata = resolved
            .Select(item => item.Metadata)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();

        return new VisualAiSourceEvidenceManifestEntry(
            sourcePrimitiveIds.Count,
            metadata.Length,
            sourcePrimitiveIds.Take(SampleLimit).ToArray(),
            resolved
                .Where(item => item.Metadata is null)
                .Select(item => item.SourceId)
                .Take(SampleLimit)
                .ToArray(),
            Distinct(metadata.Select(item => item.SourceFormat)),
            Distinct(metadata.Select(item => item.Layer)),
            Distinct(metadata.Select(item => item.EntityType)),
            Distinct(metadata.Select(item => item.BlockName)),
            Distinct(metadata.Select(item => item.Color)),
            Distinct(metadata.Select(item => item.LineType)),
            metadata
                .Select(item => item.DrawingSpace)
                .Where(item => item != SourceDrawingSpace.Unknown)
                .Select(item => item.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static IReadOnlyDictionary<string, PrimitiveSourceMetadata> BuildSourceLookup(PlanDocument document)
    {
        var result = new Dictionary<string, PrimitiveSourceMetadata>(StringComparer.Ordinal);
        foreach (var page in document.Pages)
        {
            for (var index = 0; index < page.Primitives.Count; index++)
            {
                var primitive = page.Primitives[index];
                var metadata = Normalize(primitive);
                Add(result, $"p{page.Number}:primitive:{index}", metadata);
                Add(result, primitive.SourceId, metadata);
                Add(result, primitive.Source.SourceId, metadata);
            }
        }

        return result;
    }

    private static PrimitiveSourceMetadata Normalize(PlanPrimitive primitive) =>
        primitive.Source with
        {
            SourceId = Clean(primitive.Source.SourceId) ?? Clean(primitive.SourceId),
            EntityType = Clean(primitive.Source.EntityType) ?? primitive.Kind.ToString(),
            Layer = Clean(primitive.Source.Layer) ?? Clean(primitive.Layer)
        };

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

    private static IReadOnlyList<string> Distinct(IEnumerable<string?> values) =>
        values
            .Select(Clean)
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record VisualAiCropReviewSummary(
    string Priority,
    IReadOnlyList<string> Reasons,
    string SuggestedTrainingUse)
{
    public static VisualAiCropReviewSummary From(
        VisualAiCropArtifact artifact,
        VisualAiSourceEvidenceManifestEntry sourceEvidence,
        double objectToCropAreaRatio)
    {
        var reasons = new List<string>();
        var score = 0;

        if (artifact.Category is ObjectCategory.Unknown or ObjectCategory.GenericSymbol)
        {
            reasons.Add("deterministic category is unknown/generic");
            score += 2;
        }

        if (string.Equals(artifact.DetectionKind, "object-group", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("representative crop for a grouped symbol family");
            score += 1;
        }

        if (!string.IsNullOrWhiteSpace(artifact.SymbolName))
        {
            reasons.Add("has CAD symbol/block name evidence");
            score += 2;
        }

        if (artifact.DetectedTags.Count > 0)
        {
            reasons.Add("has deterministic industrial tag evidence");
            score += 2;
        }

        if (artifact.NearbyText.Count > 0)
        {
            reasons.Add("has nearby text context");
            score += 1;
        }

        if (sourceEvidence.BlockNames.Count > 0)
        {
            reasons.Add("source metadata includes block names");
            score += 1;
        }

        if (sourceEvidence.PrimitiveCount > 50)
        {
            reasons.Add("dense source geometry; review for linework false positives");
            score += 1;
        }

        if (objectToCropAreaRatio is > 0 and < 0.12)
        {
            reasons.Add("crop contains substantial context around the detected object");
            score += 1;
        }

        if (artifact.Classification is null)
        {
            reasons.Add("no model classification attached");
        }
        else if (artifact.Classification.Confidence < 0.70)
        {
            reasons.Add("model classification is low confidence");
            score += 2;
        }
        else
        {
            reasons.Add("model classification available for audit");
            score += 1;
        }

        if (reasons.Count == 0)
        {
            reasons.Add("crop exported for traceability");
        }

        var priority = score >= 5 ? "High" : score >= 2 ? "Medium" : "Low";
        var suggestedTrainingUse = SuggestedTrainingUseFor(artifact, sourceEvidence, objectToCropAreaRatio);
        return new VisualAiCropReviewSummary(priority, reasons.Distinct(StringComparer.Ordinal).ToArray(), suggestedTrainingUse);
    }

    private static string SuggestedTrainingUseFor(
        VisualAiCropArtifact artifact,
        VisualAiSourceEvidenceManifestEntry sourceEvidence,
        double objectToCropAreaRatio)
    {
        var hasSymbolEvidence = !string.IsNullOrWhiteSpace(artifact.SymbolName)
            || artifact.DetectedTags.Count > 0
            || sourceEvidence.BlockNames.Count > 0;
        var isGroupedReviewCandidate = string.Equals(artifact.DetectionKind, "object-group", StringComparison.OrdinalIgnoreCase);
        var isGenericReviewCandidate = artifact.Category is ObjectCategory.Unknown or ObjectCategory.GenericSymbol
            || isGroupedReviewCandidate;
        var extremeDenseContext = sourceEvidence.PrimitiveCount > 120
            || (sourceEvidence.PrimitiveCount > 50 && objectToCropAreaRatio is > 0 and < 0.08);

        if (artifact.Classification is not null)
        {
            return "model-audit-candidate";
        }

        if (extremeDenseContext && !hasSymbolEvidence && !isGroupedReviewCandidate)
        {
            return "hard-negative-review";
        }

        if (isGenericReviewCandidate || hasSymbolEvidence)
        {
            return "symbol-labeling-candidate";
        }

        return "classification-training-candidate";
    }
}

public sealed record VisualAiClassificationManifestEntry(
    string Label,
    ObjectCategory Category,
    double Confidence,
    string ModelName,
    string ModelVersion,
    string InferenceEngine,
    IReadOnlyList<VisualAiAlternativeManifestEntry> Alternatives,
    IReadOnlyList<string> Evidence)
{
    public static VisualAiClassificationManifestEntry From(VisualAiClassification classification) =>
        new(
            classification.Label,
            classification.Category,
            classification.Confidence,
            classification.ModelName,
            classification.ModelVersion,
            classification.InferenceEngine,
            classification.Alternatives.Select(VisualAiAlternativeManifestEntry.From).ToArray(),
            classification.Evidence);
}

public sealed record VisualAiAlternativeManifestEntry(
    string Label,
    ObjectCategory Category,
    double Confidence,
    IReadOnlyDictionary<string, string> Evidence)
{
    public static VisualAiAlternativeManifestEntry From(VisualAiClassificationCandidate alternative) =>
        new(alternative.Label, alternative.Category, alternative.Confidence, alternative.Evidence);
}

internal static class VisualAiPngWriter
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] WriteRgbPng(VisualAiImage image)
    {
        if (image.Channels != 3)
        {
            throw new ArgumentException("Only RGB visual AI images can be written as PNG.", nameof(image));
        }

        var expectedLength = image.Width * image.Height * image.Channels;
        if (image.Pixels.Count < expectedLength)
        {
            throw new ArgumentException("Visual AI image does not contain enough RGB pixels.", nameof(image));
        }

        using var output = new MemoryStream();
        output.Write(PngSignature);
        WriteChunk(output, "IHDR", CreateIhdr(image.Width, image.Height));
        WriteChunk(output, "IDAT", CreateIdat(image));
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static byte[] CreateIhdr(int width, int height)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4, 4), height);
        data[8] = 8;
        data[9] = 2;
        data[10] = 0;
        data[11] = 0;
        data[12] = 0;
        return data;
    }

    private static byte[] CreateIdat(VisualAiImage image)
    {
        var scanlineLength = 1 + (image.Width * 3);
        var raw = new byte[scanlineLength * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            var rawOffset = y * scanlineLength;
            raw[rawOffset] = 0;
            for (var x = 0; x < image.Width * 3; x++)
            {
                raw[rawOffset + 1 + x] = image.Pixels[(y * image.Width * 3) + x];
            }
        }

        using var compressed = new MemoryStream();
        compressed.WriteByte(0x78);
        compressed.WriteByte(0x01);
        WriteUncompressedDeflateBlocks(compressed, raw);
        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(raw));
        compressed.Write(adler);
        return compressed.ToArray();
    }

    private static void WriteUncompressedDeflateBlocks(Stream stream, byte[] data)
    {
        var offset = 0;
        do
        {
            var remaining = data.Length - offset;
            var blockLength = Math.Min(remaining, ushort.MaxValue);
            var final = offset + blockLength >= data.Length;
            stream.WriteByte(final ? (byte)0x01 : (byte)0x00);
            stream.WriteByte((byte)(blockLength & 0xff));
            stream.WriteByte((byte)((blockLength >> 8) & 0xff));
            var inverse = (ushort)~blockLength;
            stream.WriteByte((byte)(inverse & 0xff));
            stream.WriteByte((byte)((inverse >> 8) & 0xff));
            stream.Write(data, offset, blockLength);
            offset += blockLength;
        }
        while (offset < data.Length);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Adler32(byte[] data)
    {
        const uint mod = 65521;
        uint a = 1;
        uint b = 0;
        foreach (var value in data)
        {
            a = (a + value) % mod;
            b = (b + a) % mod;
        }

        return (b << 16) | a;
    }

    private static uint Crc32(byte[] typeBytes, byte[] data)
    {
        var crc = 0xffffffffu;
        crc = UpdateCrc(crc, typeBytes);
        crc = UpdateCrc(crc, data);
        return crc ^ 0xffffffffu;
    }

    private static uint UpdateCrc(uint crc, byte[] bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 1
                    ? (crc >> 1) ^ 0xedb88320u
                    : crc >> 1;
            }
        }

        return crc;
    }
}
