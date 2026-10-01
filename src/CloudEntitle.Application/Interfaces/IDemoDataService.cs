using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;

namespace CloudEntitle.Application.Interfaces;

public interface IDemoDataService
{
    event EventHandler? DataChanged;
    bool IsDemoMode { get; }
    Task<DemoStatistics> LoadAsync(CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
    Task<DemoSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed record DemoStatistics(
    int CloudAccounts,
    int Identities,
    int Resources,
    int Entitlements,
    int PrivilegedIdentities,
    int RiskFindings,
    int CriticalFindings,
    DateTime? LastScan);

public sealed record DemoSnapshot(
    IReadOnlyList<CloudAccount> Accounts,
    IReadOnlyList<CloudPrincipal> Principals,
    IReadOnlyList<CloudResource> Resources,
    IReadOnlyList<Entitlement> Entitlements,
    IReadOnlyList<EffectiveAccess> EffectiveAccess,
    IReadOnlyList<RiskFindingModel> RiskFindings,
    IReadOnlyList<RecommendationModel> Recommendations,
    IReadOnlyList<Scan> Scans)
{
    public IReadOnlyList<CloudRole> Roles { get; init; } = Array.Empty<CloudRole>();
    public IReadOnlyList<CloudPermission> Permissions { get; init; } = Array.Empty<CloudPermission>();

    public DemoStatistics Statistics => new(
        Accounts.Count,
        Principals.Count,
        Resources.Count,
        Entitlements.Count,
        Principals.Count(principal => principal.IsPrivileged),
        RiskFindings.Count,
        RiskFindings.Count(finding => finding.Severity == RiskSeverity.Critical),
        Scans.OrderByDescending(scan => scan.CompletedAt).FirstOrDefault()?.CompletedAt);
}
