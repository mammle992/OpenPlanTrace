namespace OpenPlanTrace;

public sealed class PrimitiveVectorVisualAiCropProvider : IVisualAiCropProvider
{
    private readonly int _width;
    private readonly int _height;
    private readonly bool _includeTextBounds;

    public PrimitiveVectorVisualAiCropProvider(int width = 224, int height = 224, bool includeTextBounds = false)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Crop width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Crop height must be positive.");
        }

        _width = width;
        _height = height;
        _includeTextBounds = includeTextBounds;
    }

    public ValueTask<VisualAiImage?> GetCropAsync(
        PlanDocument document,
        VisualAiCropRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        var page = document.Pages.FirstOrDefault(item => item.Number == request.PageNumber);
        if (page is null)
        {
            return ValueTask.FromResult<VisualAiImage?>(null);
        }

        var pageBounds = new PlanRect(0, 0, page.Size.Width, page.Size.Height);
        var cropBounds = request.CropBounds.ClampTo(pageBounds);
        if (cropBounds.IsEmpty || cropBounds.Width <= 0 || cropBounds.Height <= 0)
        {
            return ValueTask.FromResult<VisualAiImage?>(null);
        }

        var pixels = new byte[_width * _height * 3];
        Array.Fill<byte>(pixels, 255);

        foreach (var primitive in page.Primitives.Where(primitive => primitive.Bounds.Intersects(cropBounds)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrawPrimitive(pixels, primitive, cropBounds);
        }

        return ValueTask.FromResult<VisualAiImage?>(
            VisualAiImage.Rgb(
                _width,
                _height,
                pixels,
                $"vector-raster:{document.Id}:page:{request.PageNumber}:{request.DetectionId}"));
    }

    private void DrawPrimitive(byte[] pixels, PlanPrimitive primitive, PlanRect cropBounds)
    {
        switch (primitive)
        {
            case LinePrimitive line:
                DrawPlanLine(pixels, line.Segment.Start, line.Segment.End, cropBounds, 24, 28, 32);
                break;
            case RectanglePrimitive rectangle:
                DrawRectangle(pixels, rectangle.Rectangle, cropBounds, 24, 28, 32);
                break;
            case PolylinePrimitive polyline:
                DrawPolyline(pixels, polyline, cropBounds, 24, 28, 32);
                break;
            case ArcPrimitive arc:
                DrawArc(pixels, arc, cropBounds, 24, 28, 32);
                break;
            case SymbolPrimitive symbol:
                DrawRectangle(pixels, symbol.Bounds, cropBounds, 58, 92, 170);
                DrawPlanLine(pixels, symbol.Bounds.LeftTop(), symbol.Bounds.RightBottom(), cropBounds, 58, 92, 170);
                DrawPlanLine(pixels, symbol.Bounds.RightTop(), symbol.Bounds.LeftBottom(), cropBounds, 58, 92, 170);
                break;
            case TextPrimitive text:
                if (_includeTextBounds)
                {
                    DrawRectangle(pixels, text.Bounds, cropBounds, 170, 170, 170);
                }

                break;
        }
    }

    private void DrawRectangle(
        byte[] pixels,
        PlanRect rectangle,
        PlanRect cropBounds,
        byte red,
        byte green,
        byte blue)
    {
        DrawPlanLine(pixels, new PlanPoint(rectangle.Left, rectangle.Top), new PlanPoint(rectangle.Right, rectangle.Top), cropBounds, red, green, blue);
        DrawPlanLine(pixels, new PlanPoint(rectangle.Right, rectangle.Top), new PlanPoint(rectangle.Right, rectangle.Bottom), cropBounds, red, green, blue);
        DrawPlanLine(pixels, new PlanPoint(rectangle.Right, rectangle.Bottom), new PlanPoint(rectangle.Left, rectangle.Bottom), cropBounds, red, green, blue);
        DrawPlanLine(pixels, new PlanPoint(rectangle.Left, rectangle.Bottom), new PlanPoint(rectangle.Left, rectangle.Top), cropBounds, red, green, blue);
    }

    private void DrawPolyline(
        byte[] pixels,
        PolylinePrimitive polyline,
        PlanRect cropBounds,
        byte red,
        byte green,
        byte blue)
    {
        for (var index = 1; index < polyline.Points.Count; index++)
        {
            DrawPlanLine(pixels, polyline.Points[index - 1], polyline.Points[index], cropBounds, red, green, blue);
        }

        if (polyline.Closed && polyline.Points.Count > 2)
        {
            DrawPlanLine(pixels, polyline.Points[^1], polyline.Points[0], cropBounds, red, green, blue);
        }
    }

    private void DrawArc(
        byte[] pixels,
        ArcPrimitive arc,
        PlanRect cropBounds,
        byte red,
        byte green,
        byte blue)
    {
        const int segments = 32;
        PlanPoint? previous = null;
        for (var index = 0; index <= segments; index++)
        {
            var t = index / (double)segments;
            var angle = arc.StartAngleRadians + (arc.SweepAngleRadians * t);
            var point = new PlanPoint(
                arc.Center.X + Math.Cos(angle) * arc.Radius,
                arc.Center.Y + Math.Sin(angle) * arc.Radius);
            if (previous is { } start)
            {
                DrawPlanLine(pixels, start, point, cropBounds, red, green, blue);
            }

            previous = point;
        }
    }

    private void DrawPlanLine(
        byte[] pixels,
        PlanPoint start,
        PlanPoint end,
        PlanRect cropBounds,
        byte red,
        byte green,
        byte blue)
    {
        var x0 = ToPixelX(start.X, cropBounds);
        var y0 = ToPixelY(start.Y, cropBounds);
        var x1 = ToPixelX(end.X, cropBounds);
        var y1 = ToPixelY(end.Y, cropBounds);
        DrawLine(pixels, x0, y0, x1, y1, red, green, blue);
    }

    private int ToPixelX(double x, PlanRect cropBounds) =>
        ClampToPixel((int)Math.Round((x - cropBounds.Left) / cropBounds.Width * (_width - 1)), _width);

    private int ToPixelY(double y, PlanRect cropBounds) =>
        ClampToPixel((int)Math.Round((y - cropBounds.Top) / cropBounds.Height * (_height - 1)), _height);

    private static int ClampToPixel(int value, int size) =>
        Math.Max(0, Math.Min(size - 1, value));

    private void DrawLine(
        byte[] pixels,
        int x0,
        int y0,
        int x1,
        int y1,
        byte red,
        byte green,
        byte blue)
    {
        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;

        while (true)
        {
            SetPixel(pixels, x0, y0, red, green, blue);
            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            var doubled = 2 * error;
            if (doubled >= dy)
            {
                error += dy;
                x0 += sx;
            }

            if (doubled <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    private void SetPixel(byte[] pixels, int x, int y, byte red, byte green, byte blue)
    {
        if (x < 0 || y < 0 || x >= _width || y >= _height)
        {
            return;
        }

        var offset = ((y * _width) + x) * 3;
        pixels[offset] = red;
        pixels[offset + 1] = green;
        pixels[offset + 2] = blue;
    }
}

internal static class VisualAiPlanRectExtensions
{
    public static PlanPoint LeftTop(this PlanRect rect) => new(rect.Left, rect.Top);

    public static PlanPoint RightTop(this PlanRect rect) => new(rect.Right, rect.Top);

    public static PlanPoint LeftBottom(this PlanRect rect) => new(rect.Left, rect.Bottom);

    public static PlanPoint RightBottom(this PlanRect rect) => new(rect.Right, rect.Bottom);
}
