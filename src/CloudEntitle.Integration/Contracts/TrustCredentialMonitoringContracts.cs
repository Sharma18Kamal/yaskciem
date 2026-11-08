using CloudEntitle.Domain;

namespace CloudEntitle.Integration.Contracts;

public sealed record TrustCredentialObservation(
    string PrincipalId,
    string PrincipalName,
    string Kind,
    string EvidenceId,
    string Name,
    string? Issuer,
    string? Subject,
    IReadOnlyList<string> Audiences,
    DateTimeOffset? ValidFromUtc,
    DateTimeOffset? ValidUntilUtc,
    string MetadataSha256);

public sealed record TrustCredentialSnapshot(
    Guid SnapshotId,
    CloudProvider Provider,
    string AccountId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CollectedAtUtc,
    string Source,
    IReadOnlyList<TrustCredentialObservation> Observations,
    IReadOnlyList<string> Limitations)
{
    public bool CredentialInventoryComplete { get; init; }
    public bool TrustInventoryComplete { get; init; }
    public string CoverageStatus => "Partial";
}

public sealed record TrustCredentialEvidenceQuery(CloudProvider Provider, string AccountId);

public interface ITrustCredentialEvidenceCollector
{
    Task<TrustCredentialSnapshot> CollectAsync(TrustCredentialEvidenceQuery query, CancellationToken token = default);
}

public sealed record TrustCredentialSnapshotSummary(
    Guid SnapshotId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CollectedAtUtc,
    int ObservationCount,
    bool CredentialInventoryComplete,
    bool TrustInventoryComplete,
    Guid UploadedBy);

public sealed record TrustCredentialChange(
    string PrincipalId,
    string PrincipalName,
    string Kind,
    string EvidenceId,
    string Change,
    string? BeforeSha256,
    string? AfterSha256,
    string Evidence);

public sealed record EffectivePrivilegeEscalationSignal(
    string Principal,
    string Role,
    string Scope,
    string Source,
    string Confidence,
    string Evidence);

public sealed record TrustCredentialMonitoringRequest(
    CloudProvider Provider,
    string AccountId,
    Guid? TrustCredentialSnapshotId,
    Guid? BaselineScanId,
    Guid? CurrentScanId);

public sealed record TrustCredentialMonitoringReport(
    IReadOnlyList<TrustCredentialChange> TrustCredentialChanges,
    IReadOnlyList<EffectivePrivilegeEscalationSignal> PrivilegeEscalations,
    IReadOnlyList<string> Notices);

