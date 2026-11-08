using CloudEntitle.Domain;

namespace CloudEntitle.Integration.Contracts;

/// <summary>A permission statement observed in a provider document, not an effective-access decision.</summary>
public sealed record ObservedPermissionGrant(string Principal, string SourcePrincipal, string Role,
    string SourceId, string SourceVersion, string SourceSha256, string StatementId, string Scope,
    string Action, string ResourcePattern, string Effect, bool IsExclusion, bool HasCondition, bool IsDataAction);

/// <summary>A bounded account-scoped read of current provider permission documents. No historical validity interval is asserted.</summary>
public sealed record EntitlementEvidenceSnapshot(Guid SnapshotId, CloudProvider Provider, string AccountId,
    DateTimeOffset StartedAtUtc, DateTimeOffset CollectedAtUtc, IReadOnlyList<ObservedPermissionGrant> Grants,
    IReadOnlyList<string> Limitations)
{
    public string CoverageStatus => "Partial";
}

/// <summary>Exact account and user-selected CLI profile for an explicitly consented read.</summary>
public sealed record EntitlementEvidenceQuery(CloudProvider Provider, string AccountId, string Profile);

public interface IEntitlementEvidenceCollector
{
    Task<EntitlementEvidenceSnapshot> CollectAsync(EntitlementEvidenceQuery query, CancellationToken token = default);
}

/// <summary>Saved permission document observation metadata; it is not a provider change event or a grant interval.</summary>
public sealed record EntitlementEvidenceHistoryEntry(Guid SnapshotId, DateTimeOffset StartedAtUtc,
    DateTimeOffset CollectedAtUtc, int GrantCount, Guid UploadedBy);

/// <summary>Temporal compatibility with a prior observed document, never proof of the historical authorizing grant.</summary>
public sealed record HistoricalGrantCorrelation(string EventId, string Principal, string? Permission, string? Resource,
    DateTimeOffset EventAtUtc, Guid? EvidenceSnapshotId, DateTimeOffset? ObservedAtUtc, string Status,
    IReadOnlyList<ObservedPermissionGrant> CompatibleObservedGrants);

/// <summary>Bounded historical compatibility report retaining unknowns and the observation/proof boundary.</summary>
public sealed record HistoricalAttributionReport(IReadOnlyList<HistoricalGrantCorrelation> Observations, string Notice);
