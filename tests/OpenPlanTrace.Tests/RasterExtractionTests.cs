using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class RasterExtractionTests
{
    [Fact]
    public void RasterPlanDocumentBuilder_ConvertsEvidenceToNormalizedPrimitivesWithProvenance()
    {
        var extraction = new RasterExtractionResult(
            "raster-doc",
            new[]
            {
                new RasterPageExtraction(1, new PlanSize(1000, 800))
                {
                    Dpi = 300,
                    SourceImageId = "page-image-1",
                    Text = new[]
                    {
                        new RasterTextEvidence("ROOM 101", new PlanRect(100, 120, 80, 20), new Confidence(0.87))
                        {
                            SourceId = "ocr-word-1",
                            Language = "en",
                            FontSize = 12,
                            EngineName = "TestOCR",
                            EngineVersion = "1.2.3",
                            ModelName = "local-ocr",
                            ModelVersion = "2026.06",
                            Properties = new Dictionary<string, string>
                            {
                                ["blockId"] = "text-block-7"
                            }
                        }
                    },
                    Lines = new[]
                    {
                        new RasterLineEvidence(
                            new PlanLineSegment(new PlanPoint(100, 200), new PlanPoint(500, 200)),
                            new Confidence(0.73))
                        {
                            SourceId = "linework-1",
                            StrokeWidth = 2,
                            EngineName = "TestVectorizer"
                        }
                    },
                    Polylines = new[]
                    {
                        new RasterPolylineEvidence(
                            new[]
                            {
                                new PlanPoint(100, 300),
                                new PlanPoint(160, 300),
                                new PlanPoint(160, 360),
                                new PlanPoint(100, 360)
                            },
                            new Confidence(0.66))
                        {
                            SourceId = "contour-1",
                            Closed = true,
                            StrokeWidth = 1.5
                        }
                    }
                }
            })
        {
            ExtractorName = "UnitTestRasterExtractor",
            ExtractorVersion = "0.1",
            ModelName = "unit-test-model",
            ModelVersion = "v1",
            Properties = new Dictionary<string, string>
            {
                ["threshold"] = "adaptive"
            }
        };

        var source = PlanSourceDescriptor.FromFilePath("scan.png");
        var document = RasterPlanDocumentBuilder.FromExtraction(extraction, source);

        Assert.Equal("raster-doc", document.Id);
        Assert.Equal("scan.png", document.Metadata.SourceName);
        Assert.Equal("RasterEvidence", document.Metadata.Properties["extractionKind"]);
        Assert.Equal("RasterImage", document.Metadata.Properties["sourceKind"]);
        Assert.Equal("RasterImage", document.Metadata.Properties["effectiveSourceKind"]);
        Assert.Equal(".png", document.Metadata.Properties["fileExtension"]);
        Assert.Equal("UnitTestRasterExtractor", document.Metadata.Properties["extractorName"]);
        Assert.Equal("adaptive", document.Metadata.Properties["threshold"]);
        Assert.Equal("1", document.Metadata.Properties["pageCount"]);
        Assert.Equal("1", document.Metadata.Properties["textCount"]);
        Assert.Equal("1", document.Metadata.Properties["lineCount"]);
        Assert.Equal("1", document.Metadata.Properties["polylineCount"]);
        Assert.Equal("3", document.Metadata.Properties["primitiveCount"]);
        Assert.Equal("0", document.Metadata.Properties["lowConfidenceCount"]);
        Assert.Equal("0.753333", document.Metadata.Properties["averageConfidence"]);
        Assert.Equal("page-image-1", document.Metadata.Properties["sourceImageIds"]);
        Assert.Equal("300", document.Metadata.Properties["dpiValues"]);

        var page = Assert.Single(document.Pages);
        Assert.Equal(new PlanSize(1000, 800), page.Size);

        var text = Assert.IsType<TextPrimitive>(Assert.Single(page.Primitives, primitive => primitive.Kind == PlanPrimitiveKind.Text));
        Assert.Equal("ROOM 101", text.Text);
        Assert.Equal("ocr-word-1", text.SourceId);
        Assert.Equal(SourceDrawingSpace.Raster, text.Source.DrawingSpace);
        Assert.Equal("raster", text.Source.SourceFormat);
        Assert.Equal("ocr-text", text.Source.EntityType);
        Assert.Equal("0.87", text.Source.Properties["confidence"]);
        Assert.Equal("TestOCR", text.Source.Properties["extractorName"]);
        Assert.Equal("local-ocr", text.Source.Properties["modelName"]);
        Assert.Equal("en", text.Source.Properties["language"]);
        Assert.Equal("page-image-1", text.Source.Properties["sourceImageId"]);
        Assert.Equal("adaptive", text.Source.Properties["extraction.threshold"]);

        var line = Assert.IsType<LinePrimitive>(Assert.Single(page.Primitives, primitive => primitive.Kind == PlanPrimitiveKind.Line));
        Assert.Equal("linework-1", line.SourceId);
        Assert.Equal("raster-line", line.Source.EntityType);
        Assert.Equal("TestVectorizer", line.Source.Properties["extractorName"]);
        Assert.Equal("0.73", line.Source.Properties["confidence"]);

        var polyline = Assert.IsType<PolylinePrimitive>(Assert.Single(page.Primitives, primitive => primitive.Kind == PlanPrimitiveKind.Polyline));
        Assert.True(polyline.Closed);
        Assert.Equal("raster-closed-polyline", polyline.Source.EntityType);
        Assert.Equal("0.66", polyline.Source.Properties["confidence"]);
    }

    [Fact]
    public async Task RasterExtractorBoundary_CanBeWrappedByARealLoaderWithoutFakeDetections()
    {
        var extractor = new RecordingRasterExtractor();
        var loader = new RasterPlanDocumentLoader(extractor);
        var registry = new PlanDocumentLoaderRegistry(new[] { loader });

        var document = await registry.LoadAsync(
            new MemoryStream([1, 2, 3]),
            PlanSourceDescriptor.FromFilePath("scan.tif"),
            new PlanLoadOptions
            {
                ExtractText = false,
                ExtractVectorGeometry = true
            });

        Assert.Equal("raster-loader-document", document.Id);
        Assert.Equal(PlanSourceKind.RasterImage, extractor.LastSource?.EffectiveKind);
        Assert.NotNull(extractor.LastOptions);
        Assert.False(extractor.LastOptions.ExtractText);
        Assert.True(extractor.LastOptions.ExtractLinework);
        Assert.Single(document.Pages);
        Assert.Single(document.Pages[0].Primitives);
        Assert.Equal("raster", document.Metadata.Properties["format"]);
        Assert.Equal("Raster/TestRaster", document.Metadata.Properties["loader"]);
        Assert.Equal(nameof(IRasterPlanPrimitiveExtractor), document.Metadata.Properties["raster.adapter"]);
        Assert.Equal("TestRaster", document.Metadata.Properties["raster.extractor"]);

        var capability = registry.GetCapability(PlanSourceDescriptor.FromFilePath("scan.tif"));
        Assert.True(capability.CanLoad);
        Assert.Equal(PlanSourceSupportStatus.Registered, capability.Status);
        Assert.Contains("Raster/TestRaster", capability.RegisteredLoaderNames);
    }

    [Fact]
    public async Task ScanAsync_DiagnosesRasterExtractorThatReturnsNoEvidence()
    {
        var loader = new RasterPlanDocumentLoader(new EmptyRasterExtractor());
        var document = await loader.LoadAsync(
            new MemoryStream([1, 2, 3]),
            PlanSourceDescriptor.FromFilePath("empty-scan.png"));

        Assert.Equal("0", document.Metadata.Properties["raster.primitiveCount"]);

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Contains(result.Diagnostics.Messages, message => message.Code == "raster.extraction.summary");
        var diagnostic = Assert.Single(result.Diagnostics.Messages, message => message.Code == "raster.extraction.no_primitives");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("0", diagnostic.Properties["primitiveCount"]);
        Assert.Equal("EmptyRaster", diagnostic.Properties["extractor"]);
        Assert.Contains(result.Quality.Issues, issue => issue.Code == "quality.raster_no_extracted_primitives");

        var readiness = PlanImportReadiness.FromScanResult(result);
        Assert.Contains("quality.raster_no_extracted_primitives", readiness.ReviewIssueCodes);
        Assert.Contains(
            readiness.RecommendedActions,
            action => action.Contains("emit text, linework, or polylines", StringComparison.OrdinalIgnoreCase));

        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var placementDocument = JsonDocument.Parse(placementJson);
        var placementIssue = placementDocument.RootElement.GetProperty("issues").EnumerateArray().Single(issue =>
            issue.GetProperty("code").GetString() == "quality.raster_no_extracted_primitives");
        Assert.Contains(
            "raster extractor",
            placementIssue.GetProperty("recommendedAction").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScanAsync_DiagnosesMostlyLowConfidenceRasterEvidence()
    {
        var loader = new RasterPlanDocumentLoader(new LowConfidenceRasterExtractor());
        var document = await loader.LoadAsync(
            new MemoryStream([1, 2, 3]),
            PlanSourceDescriptor.FromFilePath("weak-scan.png"));

        Assert.Equal("3", document.Metadata.Properties["raster.lowConfidenceCount"]);

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var diagnostic = Assert.Single(result.Diagnostics.Messages, message => message.Code == "raster.extraction.low_confidence");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("3", diagnostic.Properties["lowConfidenceCount"]);
        Assert.Contains("weak-text", diagnostic.SourcePrimitiveIds);
        Assert.Contains(result.Quality.Issues, issue => issue.Code == "quality.raster_low_extraction_confidence");

        var readiness = PlanImportReadiness.FromScanResult(result);
        Assert.Contains("quality.raster_low_extraction_confidence", readiness.ReviewIssueCodes);
        Assert.Contains(
            readiness.RecommendedActions,
            action => action.Contains("low-confidence raster/OCR evidence", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingRasterExtractor : IRasterPlanPrimitiveExtractor
    {
        public string Name => "TestRaster";

        public string? Version => "0.1";

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
            var result = new RasterExtractionResult(
                "raster-loader-document",
                new[]
                {
                    new RasterPageExtraction(1, new PlanSize(400, 300))
                    {
                        Text = new[]
                        {
                            new RasterTextEvidence("A", new PlanRect(20, 20, 10, 10), Confidence.High)
                        }
                    }
                })
            {
                ExtractorName = Name,
                ExtractorVersion = Version
            };

            return ValueTask.FromResult(result);
        }
    }

    private sealed class EmptyRasterExtractor : IRasterPlanPrimitiveExtractor
    {
        public string Name => "EmptyRaster";

        public string? Version => "0.0";

        public ValueTask<RasterExtractionResult> ExtractAsync(
            Stream stream,
            PlanSourceDescriptor source,
            RasterExtractionOptions? options = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RasterExtractionResult(
                "empty-raster-document",
                new[]
                {
                    new RasterPageExtraction(1, new PlanSize(400, 300))
                    {
                        SourceImageId = "empty-page-1",
                        Dpi = 300
                    }
                })
            {
                ExtractorName = Name,
                ExtractorVersion = Version
            });
    }

    private sealed class LowConfidenceRasterExtractor : IRasterPlanPrimitiveExtractor
    {
        public string Name => "WeakRaster";

        public string? Version => "0.1";

        public ValueTask<RasterExtractionResult> ExtractAsync(
            Stream stream,
            PlanSourceDescriptor source,
            RasterExtractionOptions? options = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RasterExtractionResult(
                "weak-raster-document",
                new[]
                {
                    new RasterPageExtraction(1, new PlanSize(400, 300))
                    {
                        Text = new[]
                        {
                            new RasterTextEvidence("ROOM", new PlanRect(20, 20, 60, 18), new Confidence(0.42))
                            {
                                SourceId = "weak-text"
                            }
                        },
                        Lines = new[]
                        {
                            new RasterLineEvidence(
                                new PlanLineSegment(new PlanPoint(10, 100), new PlanPoint(180, 100)),
                                new Confidence(0.44))
                            {
                                SourceId = "weak-line"
                            },
                            new RasterLineEvidence(
                                new PlanLineSegment(new PlanPoint(10, 140), new PlanPoint(180, 140)),
                                new Confidence(0.36))
                            {
                                SourceId = "weak-line-2"
                            }
                        }
                    }
                })
            {
                ExtractorName = Name,
                ExtractorVersion = Version
            });
    }
}
