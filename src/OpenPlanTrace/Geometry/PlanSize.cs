namespace OpenPlanTrace;

public readonly record struct PlanSize(double Width, double Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
