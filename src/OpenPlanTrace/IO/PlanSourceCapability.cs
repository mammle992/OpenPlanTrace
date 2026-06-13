namespace OpenPlanTrace;

public sealed record PlanSourceCapability(
    PlanSourceKind Kind,
    string Key,
    string DisplayName,
    IReadOnlyList<string> Extensions,
    PlanSourceSupportStatus Status,
    IReadOnlyList<string> RegisteredLoaderNames,
    string AdapterRequirement,
    string LicenseNote,
    string Message)
{
    public bool CanLoad => Status == PlanSourceSupportStatus.Registered;
}
