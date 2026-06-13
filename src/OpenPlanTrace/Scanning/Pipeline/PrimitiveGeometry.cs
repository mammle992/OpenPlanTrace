namespace OpenPlanTrace;

internal static class PrimitiveGeometry
{
    public static IEnumerable<PrimitiveLine> EnumerateLines(PlanPage page, ScanContext context)
    {
        for (var index = 0; index < page.Primitives.Count; index++)
        {
            var primitive = page.Primitives[index];
            var primitiveId = context.PrimitiveId(page.Number, index, primitive);

            switch (primitive)
            {
                case LinePrimitive line:
                    yield return new PrimitiveLine(line.Segment, primitiveId, primitive);
                    break;

                case RectanglePrimitive rectangle:
                    foreach (var edge in RectangleToLines(rectangle.Rectangle))
                    {
                        yield return new PrimitiveLine(edge, primitiveId, primitive);
                    }

                    break;

                case PolylinePrimitive polyline:
                    foreach (var segment in PolylineToLines(polyline))
                    {
                        yield return new PrimitiveLine(segment, primitiveId, primitive);
                    }

                    break;
            }
        }
    }

    private static IEnumerable<PlanLineSegment> RectangleToLines(PlanRect rect)
    {
        if (rect.IsEmpty)
        {
            yield break;
        }

        var topLeft = new PlanPoint(rect.Left, rect.Top);
        var topRight = new PlanPoint(rect.Right, rect.Top);
        var bottomRight = new PlanPoint(rect.Right, rect.Bottom);
        var bottomLeft = new PlanPoint(rect.Left, rect.Bottom);

        yield return new PlanLineSegment(topLeft, topRight);
        yield return new PlanLineSegment(topRight, bottomRight);
        yield return new PlanLineSegment(bottomRight, bottomLeft);
        yield return new PlanLineSegment(bottomLeft, topLeft);
    }

    private static IEnumerable<PlanLineSegment> PolylineToLines(PolylinePrimitive polyline)
    {
        if (polyline.Points.Count < 2)
        {
            yield break;
        }

        for (var index = 1; index < polyline.Points.Count; index++)
        {
            yield return new PlanLineSegment(polyline.Points[index - 1], polyline.Points[index]);
        }

        if (polyline.Closed)
        {
            yield return new PlanLineSegment(polyline.Points[^1], polyline.Points[0]);
        }
    }
}
