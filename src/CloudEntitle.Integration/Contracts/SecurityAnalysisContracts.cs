using CloudEntitle.Domain;

namespace CloudEntitle.Integration.Contracts;

/// <summary>A saved-evidence comparison; it does not poll cloud APIs or assert complete activity coverage.</summary>
public sealed record SecurityAnalysisRequest(CloudProvider Provider, string AccountId, Guid? ActivitySnapshotId, Guid? EntitlementSnapshotId);
/// <summary>A potential behavior anomaly with its explicit observation and baseline boundaries.</summary>
public sealed record IdentityBehaviorAnomaly(string EventId, string Principal, string Kind, DateTimeOffset AtUtc, string Evidence);
/// <summary>An observed document change, not a provider-attested policy change or deletion.</summary>
public sealed record ObservedEntitlementChange(string Principal, string SourceId, string Kind, string Before, string After);
/// <summary>Saved evidence review results. Missing coverage must not drive automatic permission removal.</summary>
public sealed record SecurityAnalysisReport(IReadOnlyList<IdentityBehaviorAnomaly> Anomalies,
    IReadOnlyList<ObservedEntitlementChange> Changes, IReadOnlyList<string> Notices);
/// <summary>Client configured baseline and working-time policy; privileged actions are explicit, not guessed from names.</summary>
public sealed record IdentityBehaviorPolicy(IReadOnlyList<string> PrivilegedActions, string TimeZoneId,
    int BusinessStartHour = 9, int BusinessEndHour = 18, int MinimumBaselineDays = 7, int MinimumBaselineEvents = 20)
{
    public IReadOnlyList<string> PermissionChangeActions { get; init; } = [];
    public int PermissionChangeBurstThreshold { get; init; } = 5;
}
