namespace OpenPlanTrace;

public static class PlanSourceCapabilityCatalog
{
    public static IReadOnlyList<PlanSourceCapability> Describe(IEnumerable<IPlanDocumentLoader> loaders)
    {
        ArgumentNullException.ThrowIfNull(loaders);
        var loaderArray = loaders.ToArray();

        return new[]
        {
            DescribeKnown(
                PlanSourceKind.Pdf,
                "pdf",
                "PDF",
                new[] { ".pdf" },
                loaderArray,
                PlanSourceSupportStatus.KnownButNotRegistered,
                "Register a PDF adapter such as OpenPlanTrace.Pdf.",
                "OpenPlanTrace.Pdf uses PdfPig; see third-party notices for package licensing.",
                "Vector/text PDF extraction is available when a PDF loader is registered."),

            DescribeKnown(
                PlanSourceKind.Dxf,
                "dxf",
                "DXF",
                new[] { ".dxf" },
                loaderArray,
                PlanSourceSupportStatus.KnownButNotRegistered,
                "Register a DXF adapter such as OpenPlanTrace.Dxf.",
                "OpenPlanTrace.Dxf uses IxMilia.Dxf; see third-party notices for package licensing.",
                "Open CAD exchange extraction is available when a DXF loader is registered."),

            DescribeKnown(
                PlanSourceKind.Dwg,
                "dwg",
                "DWG",
                new[] { ".dwg" },
                loaderArray,
                PlanSourceSupportStatus.OptionalAdapterRequired,
                "Register an optional native DWG adapter, or use OpenPlanTrace.Dxf.DwgToDxfPlanDocumentLoader with a real converter implementation such as a host-configured ExternalDwgToDxfConverter.",
                "Native DWG access usually requires a licensed commercial SDK, or a carefully isolated GPL-compatible bridge such as LibreDWG. The MIT core does not include DWG parsing.",
                "Native DWG is not parsed unless a real DWG loader is registered."),

            DescribeKnown(
                PlanSourceKind.RasterImage,
                "raster",
                "Raster image",
                new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp" },
                loaderArray,
                PlanSourceSupportStatus.Planned,
                "Register OpenPlanTrace.RasterPlanDocumentLoader with a real IRasterPlanPrimitiveExtractor, or provide a custom IPlanDocumentLoader for raster sources.",
                "Raster/OCR dependencies must be licensed separately by the adapter package.",
                "Raster scans require a real adapter; the core provides an extractor-backed loader and does not invent vector detections from images."),

            DescribeKnown(
                PlanSourceKind.VectorImage,
                "vector",
                "Vector image",
                new[] { ".svg" },
                loaderArray,
                PlanSourceSupportStatus.Planned,
                "Register a future SVG/vector-image adapter.",
                "Vector-image dependencies must be licensed separately by the adapter package.",
                "Vector image ingestion is planned and currently requires an adapter."),

            new PlanSourceCapability(
                PlanSourceKind.Clipboard,
                "clipboard",
                "Clipboard",
                Array.Empty<string>(),
                PlanSourceSupportStatus.Wrapper,
                LoaderNamesFor(loaderArray, PlanSourceKind.Clipboard),
                "Clipboard input routes to a registered loader for the effective clipboard content kind.",
                "Clipboard support inherits the licensing requirements of the effective content adapter.",
                "Clipboard is a source wrapper, not a separate parser."),

            DescribeKnown(
                PlanSourceKind.ExtractedPrimitives,
                "extracted-primitives",
                "Extracted primitives",
                Array.Empty<string>(),
                loaderArray,
                PlanSourceSupportStatus.KnownButNotRegistered,
                "Pass a normalized PlanDocument directly to the scanner, or register an adapter for pre-extracted primitive bundles.",
                "No third-party parsing dependency is required for already-normalized primitives.",
                "Pre-extracted primitives are supported through the scanner boundary rather than file parsing."),

            new PlanSourceCapability(
                PlanSourceKind.Unknown,
                "unknown",
                "Unknown",
                Array.Empty<string>(),
                PlanSourceSupportStatus.Unknown,
                Array.Empty<string>(),
                "Use a recognized extension or provide a PlanSourceDescriptor with a known effective kind.",
                "Unknown source licensing cannot be assessed.",
                "The input extension is not recognized by OpenPlanTrace.")
        };
    }

    public static PlanSourceCapability Describe(
        PlanSourceDescriptor source,
        IEnumerable<IPlanDocumentLoader> loaders)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Describe(loaders).FirstOrDefault(item => item.Kind == source.EffectiveKind)
            ?? Describe(loaders).First(item => item.Kind == PlanSourceKind.Unknown);
    }

    private static PlanSourceCapability DescribeKnown(
        PlanSourceKind kind,
        string key,
        string displayName,
        IReadOnlyList<string> extensions,
        IReadOnlyList<IPlanDocumentLoader> loaders,
        PlanSourceSupportStatus missingStatus,
        string adapterRequirement,
        string licenseNote,
        string message)
    {
        var registeredLoaderNames = LoaderNamesFor(loaders, kind);
        var status = registeredLoaderNames.Count > 0
            ? PlanSourceSupportStatus.Registered
            : missingStatus;

        return new PlanSourceCapability(
            kind,
            key,
            displayName,
            extensions,
            status,
            registeredLoaderNames,
            adapterRequirement,
            licenseNote,
            message);
    }

    private static IReadOnlyList<string> LoaderNamesFor(
        IReadOnlyList<IPlanDocumentLoader> loaders,
        PlanSourceKind kind) =>
        loaders
            .Where(loader => loader.SupportedSourceKinds.Contains(kind))
            .Select(loader => loader.FormatName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
