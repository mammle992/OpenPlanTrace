using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class PdfPigPlanDocumentLoaderTests
{
    [Fact]
    public async Task LoadAsync_ExtractsTextAndVectorPrimitivesFromPdf()
    {
        var loader = new PdfPigPlanDocumentLoader();
        await using var stream = new MemoryStream(CreateMinimalPdf());

        var document = await loader.LoadAsync(
            stream,
            PlanSourceDescriptor.FromFileNameOrExtension(".pdf"));

        var page = Assert.Single(document.Pages);

        Assert.Equal(300, page.Size.Width);
        Assert.Equal(200, page.Size.Height);
        Assert.Contains(page.Primitives, primitive => primitive is LinePrimitive);
        Assert.Contains(page.Primitives, primitive => primitive is RectanglePrimitive);
        Assert.Contains(page.Primitives, primitive => primitive is TextPrimitive text && text.Text == "ROOM");

        var line = page.Primitives.OfType<LinePrimitive>().First();
        Assert.Equal("pdf", line.Source.SourceFormat);
        Assert.Equal("line", line.Source.EntityType);
        Assert.Equal(SourceDrawingSpace.Paper, line.Source.DrawingSpace);
        Assert.Equal(0.5, line.Source.LineWeight);
        Assert.Equal("solid", line.Source.LineType);

        var text = page.Primitives.OfType<TextPrimitive>().First();
        Assert.Equal("pdf", text.Source.SourceFormat);
        Assert.Equal("word", text.Source.EntityType);
        Assert.Equal("1", text.Source.Properties["pageNumber"]);
    }

    [Fact]
    public async Task LoadAsync_RecordsEmbeddedImageMetadataFromImageOnlyPdf()
    {
        var loader = new PdfPigPlanDocumentLoader();
        await using var stream = new MemoryStream(CreateImageOnlyPdf());

        var document = await loader.LoadAsync(
            stream,
            PlanSourceDescriptor.FromFileNameOrExtension(".pdf"));

        var page = Assert.Single(document.Pages);

        Assert.Empty(page.Primitives);
        Assert.Equal("pdf", document.Metadata.Properties["format"]);
        Assert.Equal("Pdf", document.Metadata.Properties["sourceKind"]);
        Assert.Equal("Pdf", document.Metadata.Properties["effectiveSourceKind"]);
        Assert.Equal(".pdf", document.Metadata.Properties["fileExtension"]);
        Assert.Equal("1", document.Metadata.Properties["pdf.imageCount"]);
        Assert.Equal("1", document.Metadata.Properties["pdf.imagePageCount"]);
        Assert.Equal("1", document.Metadata.Properties["pdf.imageOnlyPageCount"]);
        Assert.Equal("1", document.Metadata.Properties["pdf.imagePages"]);
        Assert.Equal("1", document.Metadata.Properties["pdf.imageOnlyPages"]);
        Assert.Equal("1", document.Metadata.Properties["pdf.maxImagePageCoverage"]);
        Assert.Equal("4", document.Metadata.Properties["pdf.imageSamplePixels"]);
    }

    [Fact]
    public async Task ScanAsync_DiagnosesImageOnlyPdfAsRasterOcrCandidate()
    {
        var loader = new PdfPigPlanDocumentLoader();
        await using var stream = new MemoryStream(CreateImageOnlyPdf());

        var document = await loader.LoadAsync(
            stream,
            PlanSourceDescriptor.FromFileNameOrExtension(".pdf"));
        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Contains(result.Diagnostics.Messages, message => message.Code == "pdf.images.detected");
        var diagnostic = Assert.Single(result.Diagnostics.Messages, message => message.Code == "pdf.raster_image_only_pages");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("1", diagnostic.Properties["imageOnlyPageCount"]);
        Assert.Equal(nameof(IRasterPlanPrimitiveExtractor), diagnostic.Properties["adapterRequirement"]);
        Assert.Contains(result.Quality.Issues, issue => issue.Code == "quality.pdf_raster_ocr_required");

        var readiness = PlanImportReadiness.FromScanResult(result);
        Assert.Contains("quality.pdf_raster_ocr_required", readiness.ReviewIssueCodes);
        Assert.Contains(
            readiness.RecommendedActions,
            action => action.Contains("image-only PDF pages", StringComparison.OrdinalIgnoreCase)
                && action.Contains("raster/OCR adapter", StringComparison.OrdinalIgnoreCase));

        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var placementDocument = JsonDocument.Parse(placementJson);
        var placementIssue = placementDocument.RootElement.GetProperty("issues").EnumerateArray().Single(issue =>
            issue.GetProperty("code").GetString() == "quality.pdf_raster_ocr_required");
        Assert.Contains(
            "raster/OCR adapter",
            placementIssue.GetProperty("recommendedAction").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] CreateMinimalPdf()
    {
        const string content = """
            0.5 w
            50 50 m 250 50 l S
            100 100 80 40 re S
            BT /F1 12 Tf 60 150 Td (ROOM) Tj ET
            """;

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream"
        };

        var builder = new StringBuilder();
        var offsets = new List<int> { 0 };

        builder.Append("%PDF-1.4\n");

        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n");
            builder.Append(objects[index]).Append('\n');
            builder.Append("endobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());

        builder.Append("xref\n");
        builder.Append("0 ").Append(objects.Length + 1).Append('\n');
        builder.Append("0000000000 65535 f \n");

        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        builder.Append("trailer\n");
        builder.Append("<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        builder.Append("startxref\n");
        builder.Append(xrefOffset).Append('\n');
        builder.Append("%%EOF\n");

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static byte[] CreateImageOnlyPdf()
    {
        var content = "q\n300 0 0 200 0 0 cm\n/Im1 Do\nQ";
        var imageBytes = new byte[]
        {
            255, 255, 255,
            0, 0, 0,
            0, 0, 0,
            255, 255, 255
        };
        using var output = new MemoryStream();
        var offsets = new List<long> { 0 };

        WriteAscii(output, "%PDF-1.4\n");
        WriteObject(output, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(output, offsets, 2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteObject(
            output,
            offsets,
            3,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /XObject << /Im1 4 0 R >> >> /Contents 5 0 R >>");

        offsets.Add(output.Position);
        WriteAscii(
            output,
            $"4 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {imageBytes.Length} >>\nstream\n");
        output.Write(imageBytes);
        WriteAscii(output, "\nendstream\nendobj\n");

        WriteObject(output, offsets, 5, $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream");

        var xrefOffset = output.Position;
        WriteAscii(output, "xref\n");
        WriteAscii(output, $"0 {offsets.Count}\n");
        WriteAscii(output, "0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            WriteAscii(output, offset.ToString("D10", CultureInfo.InvariantCulture));
            WriteAscii(output, " 00000 n \n");
        }

        WriteAscii(output, "trailer\n");
        WriteAscii(output, $"<< /Size {offsets.Count} /Root 1 0 R >>\n");
        WriteAscii(output, "startxref\n");
        WriteAscii(output, xrefOffset.ToString(CultureInfo.InvariantCulture));
        WriteAscii(output, "\n%%EOF\n");
        return output.ToArray();
    }

    private static void WriteObject(
        Stream output,
        ICollection<long> offsets,
        int number,
        string body)
    {
        offsets.Add(output.Position);
        WriteAscii(output, $"{number} 0 obj\n{body}\nendobj\n");
    }

    private static void WriteAscii(Stream output, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        output.Write(bytes);
    }
}
