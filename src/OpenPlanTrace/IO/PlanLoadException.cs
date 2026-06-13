namespace OpenPlanTrace;

public sealed class PlanLoadException : Exception
{
    public PlanLoadException(string message)
        : base(message)
    {
    }

    public PlanLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
