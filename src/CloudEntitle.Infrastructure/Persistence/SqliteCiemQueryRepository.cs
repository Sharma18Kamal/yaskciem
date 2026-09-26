using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudEntitle.Infrastructure.Persistence;

public sealed class SqliteCiemQueryRepository : ICiemQueryRepository
{
    private readonly IDbContextFactory<CloudEntitleDbContext> _contextFactory;

    public SqliteCiemQueryRepository(IDbContextFactory<CloudEntitleDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IReadOnlyList<CloudAccount>> GetCloudAccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.CloudAccounts.AsNoTracking().OrderBy(account => account.Name).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CloudPrincipal>> GetPrivilegedPrincipalsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.CloudPrincipals.AsNoTracking().Where(principal => principal.IsPrivileged).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CloudPrincipal>> GetExternalPrincipalsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.CloudPrincipals.AsNoTracking().Where(principal => principal.IsExternal).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CloudResource>> GetResourcesForAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.CloudResources.AsNoTracking().Where(resource => resource.AccountId == accountId).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Entitlement>> GetEntitlementsForPrincipalAsync(Guid principalId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Entitlements.AsNoTracking().Where(entitlement => entitlement.PrincipalId == principalId).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EffectiveAccess>> GetEffectiveAccessForPrincipalAsync(Guid principalId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EffectiveAccesses.AsNoTracking().Where(access => access.PrincipalId == principalId).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EffectiveAccess>> GetEffectiveAccessForResourceAsync(Guid resourceId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EffectiveAccesses.AsNoTracking().Where(access => access.ResourceId == resourceId).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RiskFindingModel>> GetOpenRiskFindingsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.NormalizedRiskFindings.AsNoTracking().Where(finding => finding.Status == FindingStatus.Open).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RiskFindingModel>> GetRiskFindingsBySeverityAsync(RiskSeverity severity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.NormalizedRiskFindings.AsNoTracking().Where(finding => finding.Severity == severity).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Scan>> GetScanHistoryAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Scans.AsNoTracking().Where(scan => scan.CloudAccountId == accountId).OrderByDescending(scan => scan.StartedAt).ToListAsync(cancellationToken);
    }
}
