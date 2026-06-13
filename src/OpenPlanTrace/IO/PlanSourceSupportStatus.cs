namespace OpenPlanTrace;

public enum PlanSourceSupportStatus
{
    Unknown = 0,
    Registered,
    KnownButNotRegistered,
    OptionalAdapterRequired,
    Planned,
    Wrapper
}
