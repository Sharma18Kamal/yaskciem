namespace CloudEntitle.Integration.Contracts;

/// <summary>Observed permission-to-current-inventory correlation, never proof that a particular grant authorized a past call.</summary>
public sealed record ActivityEntitlementCorrelation(string EventId, string Principal, string Action, string? Permission,
    string? Resource, DateTimeOffset Timestamp, string Status, Guid? PrincipalId, Guid? ResourceId,
    IReadOnlyList<Guid> CompatibleEffectiveAccessIds, string Details);

public sealed record ActivityAttributionReport(IReadOnlyList<ActivityEntitlementCorrelation> Observations, string Notice);
