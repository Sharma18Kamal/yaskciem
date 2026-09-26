using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;

namespace CloudEntitle.Application.Interfaces;

public interface ICiemQueryRepository
{
    Task<IReadOnlyList<CloudAccount>> GetCloudAccountsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CloudPrincipal>> GetPrivilegedPrincipalsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CloudPrincipal>> GetExternalPrincipalsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CloudResource>> GetResourcesForAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Entitlement>> GetEntitlementsForPrincipalAsync(Guid principalId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EffectiveAccess>> GetEffectiveAccessForPrincipalAsync(Guid principalId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EffectiveAccess>> GetEffectiveAccessForResourceAsync(Guid resourceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RiskFindingModel>> GetOpenRiskFindingsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RiskFindingModel>> GetRiskFindingsBySeverityAsync(RiskSeverity severity, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Scan>> GetScanHistoryAsync(Guid accountId, CancellationToken cancellationToken = default);
}
