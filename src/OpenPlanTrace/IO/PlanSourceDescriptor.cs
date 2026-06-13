namespace OpenPlanTrace;

public sealed record PlanSourceDescriptor
{
    public PlanSourceKind Kind { get; init; } = PlanSourceKind.Unknown;

    public string? Name { get; init; }

    public string? FilePath { get; init; }

    public string? FileExtension { get; init; }

    public string? ContentType { get; init; }

    public PlanSourceKind? ClipboardContentKind { get; init; }

    public PlanSourceKind EffectiveKind =>
        Kind == PlanSourceKind.Clipboard && ClipboardContentKind is { } clipboardContentKind
            ? clipboardContentKind
            : Kind;

    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();

    public static PlanSourceDescriptor FromFilePath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var extension = Path.GetExtension(filePath);
        return new PlanSourceDescriptor
        {
            Kind = InferKind(extension),
            Name = Path.GetFileName(filePath),
            FilePath = filePath,
            FileExtension = NormalizeExtension(extension)
        };
    }

    public static PlanSourceDescriptor FromFileNameOrExtension(string fileNameOrExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileNameOrExtension);

        var extension = Path.GetExtension(fileNameOrExtension);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = fileNameOrExtension;
        }

        return new PlanSourceDescriptor
        {
            Kind = InferKind(extension),
            Name = fileNameOrExtension,
            FileExtension = NormalizeExtension(extension)
        };
    }

    public static PlanSourceDescriptor FromClipboard(
        PlanSourceKind contentKind,
        string? name = null,
        string? contentType = null) =>
        new()
        {
            Kind = PlanSourceKind.Clipboard,
            ClipboardContentKind = contentKind,
            Name = name,
            FileExtension = ExtensionFor(contentKind),
            ContentType = contentType
        };

    public static PlanSourceDescriptor FromClipboardContent(
        string? name = null,
        string? contentType = null)
    {
        var extension = Path.GetExtension(name ?? string.Empty);
        var kind = InferKindFromContentType(contentType);
        if (kind == PlanSourceKind.Unknown)
        {
            kind = InferKind(extension);
        }

        return new PlanSourceDescriptor
        {
            Kind = PlanSourceKind.Clipboard,
            ClipboardContentKind = kind,
            Name = name,
            FileExtension = NormalizeExtension(extension) ?? ExtensionForContentType(contentType) ?? ExtensionFor(kind),
            ContentType = NormalizeContentType(contentType)
        };
    }

    public bool MatchesExtension(string extension)
    {
        var normalized = NormalizeExtension(extension);
        return normalized is not null
            && string.Equals(FileExtension, normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static PlanSourceKind InferKind(string? extension) =>
        NormalizeExtension(extension) switch
        {
            ".pdf" => PlanSourceKind.Pdf,
            ".dwg" => PlanSourceKind.Dwg,
            ".dxf" => PlanSourceKind.Dxf,
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".webp" => PlanSourceKind.RasterImage,
            ".svg" => PlanSourceKind.VectorImage,
            _ => PlanSourceKind.Unknown
        };

    private static PlanSourceKind InferKindFromContentType(string? contentType) =>
        NormalizeContentType(contentType) switch
        {
            "application/pdf" => PlanSourceKind.Pdf,
            "application/dxf" or "application/x-dxf" or "image/vnd.dxf" or "drawing/x-dxf" => PlanSourceKind.Dxf,
            "application/dwg" or "application/x-dwg" or "application/acad" or "application/x-acad" or "application/autocad_dwg" or "image/vnd.dwg" => PlanSourceKind.Dwg,
            "image/png" or "image/jpeg" or "image/bmp" or "image/tiff" or "image/webp" => PlanSourceKind.RasterImage,
            "image/svg+xml" => PlanSourceKind.VectorImage,
            _ => PlanSourceKind.Unknown
        };

    private static string? ExtensionFor(PlanSourceKind kind) =>
        kind switch
        {
            PlanSourceKind.Pdf => ".pdf",
            PlanSourceKind.Dwg => ".dwg",
            PlanSourceKind.Dxf => ".dxf",
            PlanSourceKind.RasterImage => ".png",
            PlanSourceKind.VectorImage => ".svg",
            _ => null
        };

    private static string? ExtensionForContentType(string? contentType) =>
        NormalizeContentType(contentType) switch
        {
            "application/pdf" => ".pdf",
            "application/dxf" or "application/x-dxf" or "image/vnd.dxf" or "drawing/x-dxf" => ".dxf",
            "application/dwg" or "application/x-dwg" or "application/acad" or "application/x-acad" or "application/autocad_dwg" or "image/vnd.dwg" => ".dwg",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            _ => null
        };

    private static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var mediaType = contentType.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        return string.IsNullOrWhiteSpace(mediaType) ? null : mediaType.ToLowerInvariant();
    }

    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var trimmed = extension.Trim();
        return trimmed.StartsWith(".", StringComparison.Ordinal)
            ? trimmed.ToLowerInvariant()
            : $".{trimmed.ToLowerInvariant()}";
    }
}
