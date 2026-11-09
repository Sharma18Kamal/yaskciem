using CloudEntitle.Domain;
using CloudEntitle.EffectiveAccess.Models;
using CloudEntitle.Infrastructure.Persistence;
using CloudEntitle.Normalization.Models;
using Microsoft.EntityFrameworkCore;

namespace CloudEntitle.Desktop.Services;

public interface IWhatIfSavedData
{
    Task<IReadOnlyList<CloudAccount>> GetAccountsAsync(CancellationToken token);
    Task<WhatIfInput> LoadAsync(Guid accountId, CancellationToken token);
}

public sealed class WhatIfSavedData(IDbContextFactory<CloudEntitleDbContext> factory) : IWhatIfSavedData
{
    public async Task<IReadOnlyList<CloudAccount>> GetAccountsAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return await db.CloudAccounts.AsNoTracking().Where(item => item.CloudProvider != CloudProviderType.Kubernetes)
            .OrderBy(item => item.Name).ToArrayAsync(token);
    }

    public async Task<WhatIfInput> LoadAsync(Guid accountId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var account = await db.CloudAccounts.AsNoTracking().SingleAsync(item => item.Id == accountId, token);
        var sourceSnapshotAtUtc = account.LastScannedAt is { } collectedAt
            ? DateTime.SpecifyKind(collectedAt, DateTimeKind.Utc)
            : throw new InvalidOperationException("Saved inventory has no provider collection timestamp; it cannot be used as evidence for a What-if simulation.");
        var principals = await db.CloudPrincipals.AsNoTracking().Where(item => item.AccountId == accountId).ToArrayAsync(token);
        var resources = await db.CloudResources.AsNoTracking().Where(item => item.AccountId == accountId).ToArrayAsync(token);
        var roles = await db.CloudRoles.AsNoTracking().Where(item => item.AccountId == accountId).ToArrayAsync(token);
        var policies = await db.CloudPolicies.AsNoTracking().Where(item => item.AccountId == accountId).ToArrayAsync(token);
        var permissions = await db.CloudPermissions.AsNoTracking().Where(item => item.AccountId == accountId).Take(20001).ToArrayAsync(token);
        var entitlements = await db.Entitlements.AsNoTracking().Where(item => item.CloudAccountId == accountId).Take(20001).ToArrayAsync(token);
        if (permissions.Length > 20000 || entitlements.Length > 20000)
            throw new InvalidOperationException("Saved inventory exceeds 20,000 permissions/entitlements. Prepare an explicitly split offline bundle; no records were truncated for analysis.");
        var memberships = await db.GroupMemberships.AsNoTracking().Where(item => item.CloudAccountId == accountId).ToArrayAsync(token);
        var provider = (CloudProvider)account.CloudProvider;
        var now = DateTime.UtcNow;
        var grants = entitlements.Select(item =>
        {
            var permission = permissions.SingleOrDefault(value => value.Id == item.PermissionId)
                ?? throw new InvalidOperationException("Saved entitlement references a missing permission.");
            if (string.IsNullOrWhiteSpace(item.Scope))
                throw new InvalidOperationException("Saved entitlement scope is missing; export a complete offline bundle instead.");
            var evidence = new ProviderEvidence(provider, "SavedInventoryProjection", item.Source ?? item.Id.ToString("D"),
                permission.Source, null, null, item.Scope, item.AssignmentType.ToString(), sourceSnapshotAtUtc, new Dictionary<string, string>());
            return new NormalizedEntitlement(item.Id, provider, accountId, item.PrincipalId, item.ResourceId,
                item.RoleId, item.PolicyId, item.PermissionId,
                new NormalizedScope(provider, NormalizedScopeType.Unknown, item.Scope, item.Scope, null,
                    item.AssignmentType == AssignmentType.Inherited), item.AssignmentType,
                permission.AccessType == AccessType.Deny ? NormalizedAccessType.Deny : NormalizedAccessType.Allow,
                item.AssignmentType is AssignmentType.Direct or AssignmentType.Role or AssignmentType.Policy,
                item.AssignmentType == AssignmentType.Inherited, false, null, NormalizationStatus.Unknown,
                evidence, item.GrantedAt, item.ExpiresAt, permission.Resource, permission.IsExclusion);
        }).ToArray();
        return new WhatIfInput(new AccessEvaluationRequest(new NormalizationResult(principals, roles, policies, permissions,
            grants, Array.Empty<NormalizationWarning>(), Array.Empty<string>(), new(grants.Length, 0, grants.Length, 0, 0)),
            resources, memberships, EvaluationTime: now), $"Saved local inventory: {account.Name}",
            new[] { "Saved inventory does not preserve complete native policy, deny, condition or authorization coverage. Results are potential impacts only." })
        {
            SourceSnapshotAtUtc = sourceSnapshotAtUtc
        };
    }
}
