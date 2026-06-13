namespace OpenPlanTrace;

internal sealed record PrimitiveLine(
    PlanLineSegment Segment,
    string PrimitiveId,
    PlanPrimitive Primitive);
