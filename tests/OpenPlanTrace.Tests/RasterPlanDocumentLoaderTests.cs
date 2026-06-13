namespace OpenPlanTrace.Tests;

public sealed class RasterPlanDocumentLoaderTests
{
    [Fact]
    public async Task LoadAsync_DelegatesToExtractorAndPreservesClipboardRouting()
    {
        var extractor = new RecordingRasterExtractor();
        var loader = new RasterPlanDocumentLoader(
            extractor,
            new RasterExtractionOptions
            {
                TargetDpi = 300,
                PreserveIntermediateImages = true
            });

        var source = PlanSourceDescriptor.FromClipboard(
            PlanSourceKind.RasterImage,
            "clipboard-plan.png",
            "image/png");

        var document = await loader.LoadAsync(
            new MemoryStream([10, 20, 30]),
            source,
            new PlanLoadOptions
            {
                ExtractText = true,
                ExtractVectorGeometry = false
            });

        Assert.Equal("raster-source", document.Id);
        Assert.Equal(PlanSourceKind.Clipboard, extractor.LastSource?.Kind);
        Assert.Equal(PlanSourceKind.RasterImage, extractor.LastSource?.EffectiveKind);
        Assert.Equal(300, extractor.LastOptions?.TargetDpi);
        Assert.True(extractor.LastOptions?.ExtractText);
        Assert.False(extractor.LastOptions?.ExtractLinework);
        Assert.True(extractor.LastOptions?.PreserveIntermediateImages);

        Assert.Equal("clipboard-plan.png", document.Metadata.SourceName);
        Assert.Equal("raster", document.Metadata.Properties["format"]);
        Assert.Equal("Raster/Recorded Raster", document.Metadata.Properties["loader"]);
        Assert.Equal("Clipboard", document.Metadata.Properties["sourceKind"]);
        Assert.Equal("RasterImage", document.Metadata.Properties["effectiveSourceKind"]);
        Assert.Equal("RasterImage", document.Metadata.Properties["clipboardContentKind"]);
        Assert.Equal(".png", document.Metadata.Properties["fileExtension"]);
        Assert.Equal("image/png", document.Metadata.Properties["contentType"]);
        Assert.Equal("Recorded Raster", document.Metadata.Properties["extractorName"]);
        Assert.Equal("2.5", document.Metadata.Properties["extractorVersion"]);
        Assert.Equal("Recorded Raster", document.Metadata.Properties["raster.extractor"]);
        Assert.Equal("2.5", document.Metadata.Properties["raster.extractorVersion"]);
        Assert.Equal(nameof(IRasterPlanPrimitiveExtractor), document.Metadata.Properties["raster.adapter"]);
        Assert.Equal("1", document.Metadata.Properties["raster.pageCount"]);
        Assert.Equal("1", document.Metadata.Properties["raster.textCount"]);
        Assert.Equal("0", document.Metadata.Properties["raster.lineCount"]);
        Assert.Equal("1", document.Metadata.Properties["raster.polylineCount"]);
        Assert.Equal("2", document.Metadata.Properties["raster.primitiveCount"]);
        Assert.Equal("0", document.Metadata.Properties["raster.lowConfidenceCount"]);
        Assert.Equal("0.715", document.Metadata.Properties["raster.averageConfidence"]);

        var page = Assert.Single(document.Pages);
        Assert.Equal(new PlanSize(640, 480), page.Size);
        Assert.Equal(2, page.Primitives.Count);
        Assert.All(page.Primitives, primitive => Assert.Equal(SourceDrawingSpace.Raster, primitive.Source.DrawingSpace));
    }

    [Fact]
    public void CanLoad_AcceptsRasterFilesAndClipboardRasterButRejectsOtherFormats()
    {
        var loader = new RasterPlanDocumentLoader(new RecordingRasterExtractor());

        Assert.True(loader.CanLoad(PlanSourceDescriptor.FromFilePath("scan.webp")));
        Assert.True(loader.CanLoad(PlanSourceDescriptor.FromClipboard(PlanSourceKind.RasterImage)));
        Assert.False(loader.CanLoad(PlanSourceDescriptor.FromFilePath("plan.pdf")));
        Assert.False(loader.SupportedSourceKinds.Contains(PlanSourceKind.Pdf));
    }

    [Fact]
    public async Task LoadAsync_RejectsNonRasterSource()
    {
        var loader = new RasterPlanDocumentLoader(new RecordingRasterExtractor());

        var exception = await Assert.ThrowsAsync<PlanLoadException>(
            async () => await loader.LoadAsync(
                new MemoryStream([1]),
                PlanSourceDescriptor.FromFilePath("plan.dxf")));

        Assert.Contains("raster image sources", exception.Message);
    }

    [Fact]
    public void CapabilityCatalog_ReportsRasterRegisteredWhenRasterLoaderIsRegistered()
    {
        var registry = new PlanDocumentLoaderRegistry(new[]
        {
            new RasterPlanDocumentLoader(new RecordingRasterExtractor())
        });

        var raster = registry.GetCapability(PlanSourceDescriptor.FromFilePath("scan.png"));

        Assert.True(raster.CanLoad);
        Assert.Equal(PlanSourceSupportStatus.Registered, raster.Status);
        Assert.Contains("Raster/Recorded Raster", raster.RegisteredLoaderNames);
    }

    private sealed class RecordingRasterExtractor : IRasterPlanPrimitiveExtractor
    {
        public string Name => "Recorded Raster";

        public string? Version => "2.5";

        public PlanSourceDescriptor? LastSource { get; private set; }

        public RasterExtractionOptions? LastOptions { get; private set; }

        public ValueTask<RasterExtractionResult> ExtractAsync(
            Stream stream,
            PlanSourceDescriptor source,
            RasterExtractionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastSource = source;
            LastOptions = options;

            return ValueTask.FromResult(new RasterExtractionResult(
                "raster-source",
                new[]
                {
                    new RasterPageExtraction(1, new PlanSize(640, 480))
                    {
                        Dpi = options?.TargetDpi,
                        Text = new[]
                        {
                            new RasterTextEvidence(
                                "ROOM",
                                new PlanRect(100, 110, 50, 16),
                                new Confidence(0.82))
                        },
                        Lines = options?.ExtractLinework == false
                            ? Array.Empty<RasterLineEvidence>()
                            : new[]
                            {
                                new RasterLineEvidence(
                                    new PlanLineSegment(
                                        new PlanPoint(10, 20),
                                        new PlanPoint(120, 20)),
                                    new Confidence(0.7))
                            },
                        Polylines = new[]
                        {
                            new RasterPolylineEvidence(
                                new[]
                                {
                                    new PlanPoint(200, 200),
                                    new PlanPoint(250, 200),
                                    new PlanPoint(250, 240)
                                },
                                new Confidence(0.61))
                        }
                    }
                }));
        }
    }
}
