namespace OpenPlanTrace.Tests;

public sealed class GeometryTests
{
    [Fact]
    public void LineBounds_CanIntersectWithInflatedRegions()
    {
        var line = new PlanLineSegment(new PlanPoint(10, 20), new PlanPoint(100, 20));
        var region = new PlanRect(0, 0, 200, 200);

        Assert.False(line.Bounds.IsEmpty);
        Assert.True(region.Intersects(line.Bounds.Inflate(1)));
    }

    [Fact]
    public void TryIntersect_DoesNotExtendSegmentsByDrawingToleranceAsParameter()
    {
        var horizontal = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(500, 100));
        var vertical = new PlanLineSegment(new PlanPoint(300, 250), new PlanPoint(300, 400));

        var intersects = GeometryOperations.TryIntersect(horizontal, vertical, 2, out _);

        Assert.False(intersects);
    }

    [Fact]
    public void TryIntersect_AllowsSmallEndpointSnapInDrawingUnits()
    {
        var horizontal = new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(500, 100));
        var vertical = new PlanLineSegment(new PlanPoint(500.75, 100), new PlanPoint(500.75, 300));

        var intersects = GeometryOperations.TryIntersect(horizontal, vertical, 2, out var point);

        Assert.True(intersects);
        Assert.Equal(500, point.X, 1);
        Assert.Equal(100, point.Y, 1);
    }
}
