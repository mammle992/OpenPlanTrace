using System.Globalization;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;

namespace OpenPlanTrace.Pdf;

public sealed class PdfPigPlanDocumentLoader : PlanDocumentLoaderBase
{
    public PdfPigPlanDocumentLoader()
        : base("PDF/PdfPig", PlanSourceKind.Pdf)
    {
    }

    public override ValueTask<PlanDocument> LoadAsync(
        Stream stream,
        PlanSourceDescriptor source,
        PlanLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(source);

        options ??= new PlanLoadOptions();

        using var document = PdfDocument.Open(stream);
        var pages = new List<PlanPage>();
        var imageSummaries = new List<PdfPageImageSummary>();

        foreach (var pdfPage in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageSize = new PlanSize(pdfPage.Width, pdfPage.Height);
            var primitives = new List<PlanPrimitive>();

            var documentSource = new DocumentSourceInfo(
                source.Name ?? source.FilePath ?? "pdf-document",
                source.Name,
                source.FilePath);

            if (options.ExtractVectorGeometry)
            {
                ExtractPaths(pdfPage, primitives, documentSource);
            }

            if (options.ExtractText)
            {
                ExtractWords(pdfPage, primitives, documentSource);
            }

            var images = ExtractImageSummary(pdfPage, primitives.Count);
            imageSummaries.Add(images);
            pages.Add(new PlanPage(pdfPage.Number, pageSize, primitives));
        }

        var planDocument = new PlanDocument(
            source.Name ?? source.FilePath ?? "pdf-document",
            pages)
        {
            Metadata = new PlanMetadata
            {
                SourceName = source.Name,
                SourcePath = source.FilePath,
                Properties = CreateMetadataProperties(source, pages.Count, imageSummaries)
            }
        };

        return ValueTask.FromResult(planDocument);
    }

    private IReadOnlyDictionary<string, string> CreateMetadataProperties(
        PlanSourceDescriptor source,
        int pageCount,
        IReadOnlyList<PdfPageImageSummary> imageSummaries)
    {
        var properties = new Dictionary<string, string>
        {
            ["format"] = "pdf",
            ["loader"] = FormatName,
            ["sourceKind"] = source.Kind.ToString(),
            ["effectiveSourceKind"] = source.EffectiveKind.ToString(),
            ["pageCount"] = pageCount.ToString(CultureInfo.InvariantCulture)
        };
        AddSourceDescriptorProperties(properties, source);

        var imageCount = imageSummaries.Sum(summary => summary.ImageCount);
        if (imageCount == 0)
        {
            properties["pdf.imageCount"] = "0";
            properties["pdf.imagePageCount"] = "0";
            properties["pdf.imageOnlyPageCount"] = "0";
            return properties;
        }

        var imagePages = imageSummaries
            .Where(summary => summary.ImageCount > 0)
            .Select(summary => summary.PageNumber)
            .ToArray();
        var imageOnlyPages = imageSummaries
            .Where(summary => summary.ImageCount > 0 && summary.PrimitiveCount == 0)
            .Select(summary => summary.PageNumber)
            .ToArray();
        var maxCoverage = imageSummaries.Max(summary => summary.ImageCoverageRatio);
        properties["pdf.imageCount"] = imageCount.ToString(CultureInfo.InvariantCulture);
        properties["pdf.imagePageCount"] = imagePages.Length.ToString(CultureInfo.InvariantCulture);
        properties["pdf.imageOnlyPageCount"] = imageOnlyPages.Length.ToString(CultureInfo.InvariantCulture);
        properties["pdf.imagePages"] = string.Join(",", imagePages);
        properties["pdf.imageOnlyPages"] = string.Join(",", imageOnlyPages);
        properties["pdf.maxImagePageCoverage"] = FormatRatio(maxCoverage);
        properties["pdf.imageSamplePixels"] = imageSummaries.Sum(summary => summary.SamplePixels).ToString(CultureInfo.InvariantCulture);
        properties["pdf.imageMaskCount"] = imageSummaries.Sum(summary => summary.ImageMaskCount).ToString(CultureInfo.InvariantCulture);
        properties["pdf.inlineImageCount"] = imageSummaries.Sum(summary => summary.InlineImageCount).ToString(CultureInfo.InvariantCulture);
        return properties;
    }

    private static void AddSourceDescriptorProperties(
        IDictionary<string, string> properties,
        PlanSourceDescriptor source)
    {
        Add(properties, "fileExtension", source.FileExtension);
        Add(properties, "contentType", source.ContentType);

        if (source.ClipboardContentKind is { } clipboardContentKind)
        {
            properties["clipboardContentKind"] = clipboardContentKind.ToString();
        }

        foreach (var (key, value) in source.Properties)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                properties[$"source.{key.Trim()}"] = value.Trim();
            }
        }
    }

    private static void Add(IDictionary<string, string> properties, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            properties[key] = value.Trim();
        }
    }

    private static PdfPageImageSummary ExtractImageSummary(Page pdfPage, int primitiveCount)
    {
        var imageCount = 0;
        var imageMaskCount = 0;
        var inlineImageCount = 0;
        var samplePixels = 0L;
        var imageArea = 0.0;
        var pageArea = Math.Max(0, pdfPage.Width * pdfPage.Height);

        foreach (var image in pdfPage.GetImages())
        {
            imageCount++;
            if (image.IsImageMask)
            {
                imageMaskCount++;
            }

            if (image.IsInlineImage)
            {
                inlineImageCount++;
            }

            samplePixels += Math.Max(0, image.WidthInSamples) * (long)Math.Max(0, image.HeightInSamples);
            var bounds = ConvertRectangle(image.BoundingBox, pdfPage.Height);
            imageArea += bounds.Area;
        }

        return new PdfPageImageSummary(
            pdfPage.Number,
            imageCount,
            primitiveCount,
            imageMaskCount,
            inlineImageCount,
            samplePixels,
            pageArea <= 0 ? 0 : Math.Round(Math.Clamp(imageArea / pageArea, 0, 1), 6));
    }

    private static void ExtractWords(
        Page pdfPage,
        List<PlanPrimitive> primitives,
        DocumentSourceInfo documentSource)
    {
        var wordIndex = 0;

        foreach (var word in pdfPage.GetWords())
        {
            var text = word.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var bounds = ConvertRectangle(word.BoundingBox, pdfPage.Height);
            if (bounds.IsEmpty)
            {
                continue;
            }

            var fontSize = word.Letters.Count == 0
                ? 0
                : word.Letters.Select(letter => letter.FontSize).DefaultIfEmpty(0).Average();

            var sourceId = $"pdf:p{pdfPage.Number}:word:{++wordIndex}";
            var firstLetter = word.Letters.FirstOrDefault();

            primitives.Add(
                new TextPrimitive(text, bounds)
                {
                    SourceId = sourceId,
                    FontSize = fontSize,
                    Source = CreateSource(
                        documentSource,
                        sourceId,
                        entityType: "word",
                        layer: null,
                        color: firstLetter?.FillColor?.ToString() ?? firstLetter?.Color?.ToString(),
                        lineType: null,
                        lineWeight: null,
                        blockName: null,
                        new Dictionary<string, string>
                        {
                            ["pageNumber"] = pdfPage.Number.ToString(),
                            ["fontName"] = word.FontName ?? string.Empty,
                            ["textOrientation"] = word.TextOrientation.ToString()
                        })
                });
        }
    }

    private static void ExtractPaths(
        Page pdfPage,
        List<PlanPrimitive> primitives,
        DocumentSourceInfo documentSource)
    {
        var pathIndex = 0;

        foreach (var path in pdfPage.Paths)
        {
            pathIndex++;

            if (!path.IsStroked && !path.IsFilled)
            {
                continue;
            }

            for (var subpathIndex = 0; subpathIndex < path.Count; subpathIndex++)
            {
                var subpath = path[subpathIndex];
                var sourceId = $"pdf:p{pdfPage.Number}:path:{pathIndex}:subpath:{subpathIndex + 1}";

                if (TryExtractRectangle(subpath, pdfPage.Height, sourceId, path, documentSource, pdfPage.Number, out var rectangle))
                {
                    primitives.Add(rectangle);
                    continue;
                }

                foreach (var primitive in ExtractSubpathLines(subpath, pdfPage.Height, sourceId, path, documentSource, pdfPage.Number))
                {
                    primitives.Add(primitive);
                }
            }
        }
    }

    private static bool TryExtractRectangle(
        PdfSubpath subpath,
        double pageHeight,
        string sourceId,
        PdfPath path,
        DocumentSourceInfo documentSource,
        int pageNumber,
        out RectanglePrimitive rectangle)
    {
        var drawnRectangle = subpath.GetDrawnRectangle();
        if (drawnRectangle is null)
        {
            rectangle = default!;
            return false;
        }

        var bounds = ConvertRectangle(drawnRectangle.Value, pageHeight);
        if (bounds.IsEmpty)
        {
            rectangle = default!;
            return false;
        }

        rectangle = new RectanglePrimitive(bounds)
        {
            SourceId = sourceId,
            StrokeWidth = path.LineWidth,
            Source = CreatePathSource(documentSource, sourceId, "rectangle", path, pageNumber)
        };

        return true;
    }

    private static IEnumerable<PlanPrimitive> ExtractSubpathLines(
        PdfSubpath subpath,
        double pageHeight,
        string sourceId,
        PdfPath path,
        DocumentSourceInfo documentSource,
        int pageNumber)
    {
        var polylinePoints = new List<PlanPoint>();
        var lineIndex = 0;

        foreach (var command in subpath.Commands)
        {
            switch (command)
            {
                case PdfSubpath.Line line:
                    var start = ConvertPoint(line.From, pageHeight);
                    var end = ConvertPoint(line.To, pageHeight);

                    if (start.DistanceTo(end) <= double.Epsilon)
                    {
                        continue;
                    }

                    lineIndex++;
                    yield return new LinePrimitive(new PlanLineSegment(start, end))
                    {
                        SourceId = $"{sourceId}:line:{lineIndex}",
                        StrokeWidth = path.LineWidth,
                        Source = CreatePathSource(documentSource, $"{sourceId}:line:{lineIndex}", "line", path, pageNumber)
                    };

                    AppendPolylinePoint(polylinePoints, start);
                    AppendPolylinePoint(polylinePoints, end);
                    break;

                case PdfSubpath.Move move:
                    AppendPolylinePoint(polylinePoints, ConvertPoint(move.Location, pageHeight));
                    break;

                case PdfSubpath.QuadraticBezierCurve quadratic:
                    foreach (var point in ApproximateQuadraticBezier(quadratic, pageHeight))
                    {
                        AppendPolylinePoint(polylinePoints, point);
                    }

                    break;

                case PdfSubpath.CubicBezierCurve cubic:
                    foreach (var point in ApproximateCubicBezier(cubic, pageHeight))
                    {
                        AppendPolylinePoint(polylinePoints, point);
                    }

                    break;
            }
        }

        if (polylinePoints.Count >= 3 && !subpath.IsDrawnAsRectangle)
        {
            yield return new PolylinePrimitive(polylinePoints, subpath.IsClosed())
            {
                SourceId = $"{sourceId}:polyline",
                StrokeWidth = path.LineWidth,
                Source = CreatePathSource(documentSource, $"{sourceId}:polyline", "polyline", path, pageNumber)
            };
        }
    }

    private static PrimitiveSourceMetadata CreatePathSource(
        DocumentSourceInfo documentSource,
        string sourceId,
        string entityType,
        PdfPath path,
        int pageNumber)
    {
        var properties = new Dictionary<string, string>
        {
            ["pageNumber"] = pageNumber.ToString(),
            ["isStroked"] = path.IsStroked.ToString(),
            ["isFilled"] = path.IsFilled.ToString(),
            ["isClipping"] = path.IsClipping.ToString(),
            ["lineCapStyle"] = path.LineCapStyle.ToString(),
            ["lineJoinStyle"] = path.LineJoinStyle.ToString()
        };

        if (path.LineDashPattern is not null)
        {
            properties["lineDashPattern"] = path.LineDashPattern.ToString() ?? string.Empty;
        }

        return CreateSource(
            documentSource,
            sourceId,
            entityType,
            layer: null,
            color: path.StrokeColor?.ToString() ?? path.FillColor?.ToString(),
            lineType: IsDashed(path) ? "dashed" : "solid",
            lineWeight: path.LineWidth,
            blockName: null,
            properties);
    }

    private static bool IsDashed(PdfPath path) =>
        path.LineDashPattern is { } dashPattern && dashPattern.Array.Count > 0;

    private static PrimitiveSourceMetadata CreateSource(
        DocumentSourceInfo documentSource,
        string sourceId,
        string entityType,
        string? layer,
        string? color,
        string? lineType,
        double? lineWeight,
        string? blockName,
        IReadOnlyDictionary<string, string> properties) =>
        new()
        {
            SourceFormat = "pdf",
            SourceDocumentId = documentSource.DocumentId,
            SourceName = documentSource.SourceName,
            SourcePath = documentSource.SourcePath,
            SourceId = sourceId,
            EntityType = entityType,
            Layer = layer,
            Color = color,
            LineType = lineType,
            LineWeight = lineWeight,
            DrawingSpace = SourceDrawingSpace.Paper,
            BlockName = blockName,
            Properties = properties
        };

    private static IEnumerable<PlanPoint> ApproximateQuadraticBezier(
        PdfSubpath.QuadraticBezierCurve curve,
        double pageHeight)
    {
        var p0 = curve.StartPoint;
        var p1 = curve.ControlPoint;
        var p2 = curve.EndPoint;

        for (var index = 1; index <= 8; index++)
        {
            var t = index / 8.0;
            var x = ((1 - t) * (1 - t) * p0.X) + (2 * (1 - t) * t * p1.X) + (t * t * p2.X);
            var y = ((1 - t) * (1 - t) * p0.Y) + (2 * (1 - t) * t * p1.Y) + (t * t * p2.Y);
            yield return ConvertPoint(new PdfPoint(x, y), pageHeight);
        }
    }

    private static IEnumerable<PlanPoint> ApproximateCubicBezier(
        PdfSubpath.CubicBezierCurve curve,
        double pageHeight)
    {
        var p0 = curve.StartPoint;
        var p1 = curve.FirstControlPoint;
        var p2 = curve.SecondControlPoint;
        var p3 = curve.EndPoint;

        for (var index = 1; index <= 10; index++)
        {
            var t = index / 10.0;
            var oneMinusT = 1 - t;
            var x = (oneMinusT * oneMinusT * oneMinusT * p0.X)
                + (3 * oneMinusT * oneMinusT * t * p1.X)
                + (3 * oneMinusT * t * t * p2.X)
                + (t * t * t * p3.X);
            var y = (oneMinusT * oneMinusT * oneMinusT * p0.Y)
                + (3 * oneMinusT * oneMinusT * t * p1.Y)
                + (3 * oneMinusT * t * t * p2.Y)
                + (t * t * t * p3.Y);

            yield return ConvertPoint(new PdfPoint(x, y), pageHeight);
        }
    }

    private static void AppendPolylinePoint(List<PlanPoint> points, PlanPoint point)
    {
        if (points.Count == 0 || points[^1].DistanceTo(point) > 0.01)
        {
            points.Add(point);
        }
    }

    private static PlanPoint ConvertPoint(PdfPoint point, double pageHeight) =>
        new(point.X, pageHeight - point.Y);

    private static PlanRect ConvertRectangle(PdfRectangle rectangle, double pageHeight) =>
        PlanRect.FromEdges(
            rectangle.Left,
            pageHeight - rectangle.Top,
            rectangle.Right,
            pageHeight - rectangle.Bottom);

    private static string FormatRatio(double value) =>
        Math.Round(Math.Clamp(value, 0, 1), 6).ToString("0.######", CultureInfo.InvariantCulture);

    private sealed record DocumentSourceInfo(
        string DocumentId,
        string? SourceName,
        string? SourcePath);

    private sealed record PdfPageImageSummary(
        int PageNumber,
        int ImageCount,
        int PrimitiveCount,
        int ImageMaskCount,
        int InlineImageCount,
        long SamplePixels,
        double ImageCoverageRatio);
}
