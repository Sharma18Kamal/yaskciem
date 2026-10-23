using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CloudEntitle.Infrastructure.Persistence;

public sealed class DemoDataService : IDemoDataService
{
    private const string DemoPrefix = "demo-";
    private readonly IDbContextFactory<CloudEntitleDbContext> _contextFactory;
    private bool _isDemoMode;

    public DemoDataService(IDbContextFactory<CloudEntitleDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public event EventHandler? DataChanged;
    public bool IsDemoMode => _isDemoMode;

    public async Task<DemoStatistics> LoadAsync(CancellationToken cancellationToken = default)
    {
        await ResetAsync(cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var generated = GenerateData();
        context.CloudAccounts.AddRange(generated.Accounts);
        context.CloudPrincipals.AddRange(generated.Principals);
        context.CloudGroups.AddRange(generated.Groups);
        context.CloudResources.AddRange(generated.Resources);
        context.CloudRoles.AddRange(generated.Roles);
        context.CloudPolicies.AddRange(generated.Policies);
        context.CloudPermissions.AddRange(generated.Permissions);
        context.GroupMemberships.AddRange(generated.GroupMemberships);
        context.Entitlements.AddRange(generated.Entitlements);
        context.EffectiveAccesses.AddRange(generated.EffectiveAccess);
        context.AccessPaths.AddRange(generated.AccessPaths);
        context.AccessPathNodes.AddRange(generated.AccessPathNodes);
        context.ActivityRecords.AddRange(generated.ActivityRecords);
        context.NormalizedRiskFindings.AddRange(generated.RiskFindings);
        context.NormalizedRecommendations.AddRange(generated.Recommendations);
        context.Scans.AddRange(generated.Scans);
        await context.SaveChangesAsync(cancellationToken);
        _isDemoMode = true;
        DataChanged?.Invoke(this, EventArgs.Empty);
        Log.Information("Loaded deterministic synthetic Demo Mode data for Azure, AWS, and GCP");
        return await GetStatisticsAsync(context, cancellationToken);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await context.CloudAccounts.Where(account => account.ExternalAccountId != null && account.ExternalAccountId.StartsWith(DemoPrefix)).ToListAsync(cancellationToken);
        var accountIds = accounts.Select(account => account.Id).ToHashSet();
        if (accountIds.Count == 0)
        {
            _isDemoMode = false;
            return;
        }

        var demoFindingIds = await context.NormalizedRiskFindings.Where(item => accountIds.Contains(item.CloudAccountId)).Select(item => item.Id).ToListAsync(cancellationToken);
        context.GroupMemberships.RemoveRange(await context.GroupMemberships.Where(item => accountIds.Contains(item.CloudAccountId)).ToListAsync(cancellationToken));
        context.CloudGroups.RemoveRange(await context.CloudGroups.Where(item => accountIds.Contains(item.AccountId)).ToListAsync(cancellationToken));
        context.NormalizedRecommendations.RemoveRange(await context.NormalizedRecommendations.Where(item => demoFindingIds.Contains(item.RiskFindingId)).ToListAsync(cancellationToken));
        var demoPathIds = await context.AccessPaths.Where(item => accountIds.Contains(item.CloudAccountId)).Select(item => item.Id).ToListAsync(cancellationToken);
        context.AccessPathNodes.RemoveRange(await context.AccessPathNodes.Where(item => demoPathIds.Contains(item.AccessPathId)).ToListAsync(cancellationToken));
        context.EffectiveAccesses.RemoveRange(await context.EffectiveAccesses.Where(item => accountIds.Contains(item.CloudAccountId)).ToListAsync(cancellationToken));
        context.Entitlements.RemoveRange(await context.Entitlements.Where(item => accountIds.Contains(item.CloudAccountId)).ToListAsync(cancellationToken));
        context.AccessPaths.RemoveRange(await context.AccessPaths.Where(item => accountIds.Contains(item.CloudAccountId)).ToListAsync(cancellationToken));
        context.ActivityRecords.RemoveRange(await context.ActivityRecords.Where(item => accountIds.Contains(item.CloudAccountId)).ToListAsync(cancellationToken));
        context.Scans.RemoveRange(await context.Scans.Where(item => accountIds.Contains(item.CloudAccountId)).ToListAsync(cancellationToken));
        context.NormalizedRiskFindings.RemoveRange(await context.NormalizedRiskFindings.Where(item => accountIds.Contains(item.CloudAccountId)).ToListAsync(cancellationToken));
        context.CloudPermissions.RemoveRange(await context.CloudPermissions.Where(item => accountIds.Contains(item.AccountId)).ToListAsync(cancellationToken));
        context.CloudPolicies.RemoveRange(await context.CloudPolicies.Where(item => accountIds.Contains(item.AccountId)).ToListAsync(cancellationToken));
        context.CloudRoles.RemoveRange(await context.CloudRoles.Where(item => accountIds.Contains(item.AccountId)).ToListAsync(cancellationToken));
        context.CloudResources.RemoveRange(await context.CloudResources.Where(item => accountIds.Contains(item.AccountId)).ToListAsync(cancellationToken));
        context.CloudPrincipals.RemoveRange(await context.CloudPrincipals.Where(item => accountIds.Contains(item.AccountId)).ToListAsync(cancellationToken));
        context.CloudAccounts.RemoveRange(accounts);
        await context.SaveChangesAsync(cancellationToken);
        _isDemoMode = false;
        DataChanged?.Invoke(this, EventArgs.Empty);
        Log.Information("Reset synthetic Demo Mode data without removing real cloud records");
    }

    public Task<DemoSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        // SQLite async query methods execute synchronously; keep snapshot reads off the WPF dispatcher.
        => Task.Run(() => ReadSnapshotAsync(cancellationToken), cancellationToken);

    private async Task<DemoSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await context.CloudAccounts.AsNoTracking().Where(account => account.ExternalAccountId != null && account.ExternalAccountId.StartsWith(DemoPrefix)).ToListAsync(cancellationToken);
        var ids = accounts.Select(account => account.Id).ToHashSet();
        return new DemoSnapshot(
            accounts,
            await context.CloudPrincipals.AsNoTracking().Where(item => ids.Contains(item.AccountId)).ToListAsync(cancellationToken),
            await context.CloudResources.AsNoTracking().Where(item => ids.Contains(item.AccountId)).ToListAsync(cancellationToken),
            await context.Entitlements.AsNoTracking().Where(item => ids.Contains(item.CloudAccountId)).ToListAsync(cancellationToken),
            await context.EffectiveAccesses.AsNoTracking().Where(item => ids.Contains(item.CloudAccountId)).ToListAsync(cancellationToken),
            await context.NormalizedRiskFindings.AsNoTracking().Where(item => ids.Contains(item.CloudAccountId)).ToListAsync(cancellationToken),
            await context.NormalizedRecommendations.AsNoTracking().Where(item => context.NormalizedRiskFindings.Where(finding => ids.Contains(finding.CloudAccountId)).Select(finding => finding.Id).Contains(item.RiskFindingId)).ToListAsync(cancellationToken),
            await context.Scans.AsNoTracking().Where(item => ids.Contains(item.CloudAccountId)).ToListAsync(cancellationToken))
        {
            Roles = await context.CloudRoles.AsNoTracking().Where(item => ids.Contains(item.AccountId)).ToListAsync(cancellationToken),
            Permissions = await context.CloudPermissions.AsNoTracking().Where(item => ids.Contains(item.AccountId)).ToListAsync(cancellationToken),
            GroupMemberships = await context.GroupMemberships.AsNoTracking().Where(item => ids.Contains(item.CloudAccountId)).ToListAsync(cancellationToken),
            AccessPaths = await context.AccessPaths.AsNoTracking().Where(item => ids.Contains(item.CloudAccountId)).ToListAsync(cancellationToken)
        };
    }

    private async Task<DemoStatistics> GetStatisticsAsync(CloudEntitleDbContext context, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.Statistics;
    }

    private static GeneratedDemoData GenerateData()
    {
        var now = DateTime.UtcNow;
        var accounts = new List<CloudAccount>();
        var principals = new List<CloudPrincipal>();
        var groups = new List<CloudGroup>();
        var groupMemberships = new List<GroupMembership>();
        var resources = new List<CloudResource>();
        var roles = new List<CloudRole>();
        var policies = new List<CloudPolicy>();
        var permissions = new List<CloudPermission>();
        var entitlements = new List<Entitlement>();
        var effective = new List<EffectiveAccess>();
        var paths = new List<AccessPath>();
        var pathNodes = new List<AccessPathNode>();
        var activity = new List<ActivityRecord>();
        var findings = new List<RiskFindingModel>();
        var recommendations = new List<RecommendationModel>();
        var scans = new List<Scan>();
        var providers = new[] { (CloudProviderType.Azure, "Azure", "demo-azure-subscription"), (CloudProviderType.Aws, "AWS", "demo-aws-account"), (CloudProviderType.Gcp, "GCP", "demo-gcp-project") };

        foreach (var (provider, name, externalId) in providers)
        {
            var account = new CloudAccount { Id = Stable(provider, "account"), Name = $"{name} Demo Environment", CloudProvider = provider, ExternalAccountId = externalId, AccountType = provider == CloudProviderType.Gcp ? AccountType.Project : AccountType.Subscription, ConnectionStatus = ConnectionStatus.Connected, IsReadOnly = true, CreatedAt = now.AddDays(-90), LastScannedAt = now.AddMinutes(-8) };
            accounts.Add(account);
            var resource = new CloudResource { Id = Stable(provider, "production"), AccountId = account.Id, Name = $"{name} Demo Production", Type = "Production Resource", ResourceId = $"demo-{name.ToLowerInvariant()}-production", ResourceType = ResourceType.Resource, Region = "demo-region" };
            resources.Add(resource);
            var role = new CloudRole { Id = Stable(provider, "owner-role"), AccountId = account.Id, Name = provider == CloudProviderType.Aws ? "AdministratorAccess" : "Owner", RoleId = $"demo-{name.ToLowerInvariant()}-owner", Description = "Synthetic privileged demonstration role", IsPrivileged = true };
            roles.Add(role);
            var group = new CloudGroup { Id = Stable(provider, "developers-group"), AccountId = account.Id, Name = $"{name} Demo Developers", GroupId = $"demo-{name.ToLowerInvariant()}-developers" };
            groups.Add(group);
            principals.Add(new CloudPrincipal { Id = group.Id, AccountId = account.Id, CloudProvider = provider, Name = group.Name, PrincipalId = group.GroupId, IdentityType = IdentityType.Group, IsActive = true });
            var policy = new CloudPolicy { Id = Stable(provider, "policy"), AccountId = account.Id, Name = $"{name} Demo Wildcard Policy", ExternalId = $"demo-{name.ToLowerInvariant()}-policy", PolicyType = "Synthetic", Document = "{\"Statement\":[{\"Action\":\"*\",\"Resource\":\"*\"}]}" };
            policies.Add(policy);
            var permission = new CloudPermission { Id = Stable(provider, "wildcard"), AccountId = account.Id, Name = "Demo wildcard permission", Action = "*", Resource = "*", Source = "Synthetic demo policy", AccessType = AccessType.Allow };
            permissions.Add(permission);
            var identityNames = new[] { $"{name} Demo Administrator", $"{name} Demo Developer", $"{name} Demo External User", $"{name} Demo Service Account" };
            for (var index = 0; index < identityNames.Length; index++)
            {
                var principal = new CloudPrincipal { Id = Stable(provider, $"principal-{index}"), AccountId = account.Id, CloudProvider = provider, Name = identityNames[index], PrincipalId = $"demo-{name.ToLowerInvariant()}-principal-{index}", IdentityType = index == 3 ? IdentityType.ServiceAccount : IdentityType.User, Email = $"{name.ToLowerInvariant()}.demo.{index}@example.invalid", IsActive = index != 2, IsPrivileged = index is 0 or 3, IsExternal = index == 2, LastActivityAt = index == 2 ? now.AddDays(-220) : now.AddDays(-2) };
                principals.Add(principal);
                if (index == 1)
                {
                    group.MemberPrincipalIds.Add(principal.Id);
                    groupMemberships.Add(new GroupMembership { Id = Stable(provider, "developers-membership"), GroupId = group.Id, PrincipalId = principal.Id, CloudAccountId = account.Id, CreatedAt = now.AddDays(-60) });
                }
                var entitlement = new Entitlement { Id = Stable(provider, $"entitlement-{index}"), PrincipalId = index == 1 ? group.Id : principal.Id, RoleId = role.Id, PolicyId = policy.Id, PermissionId = permission.Id, CloudAccountId = account.Id, ResourceId = resource.Id, Scope = "demo-production", AssignmentType = index == 1 ? AssignmentType.Group : AssignmentType.Direct, GrantedAt = now.AddDays(-30), Source = "Synthetic Demo Mode" };
                entitlements.Add(entitlement);
                var access = new EffectiveAccess { Id = Stable(provider, $"access-{index}"), PrincipalId = principal.Id, CloudAccountId = account.Id, ResourceId = resource.Id, PermissionId = permission.Id, RoleId = role.Id, PolicyId = policy.Id, Scope = "demo-production", AccessType = AccessType.Allow, AssignmentType = entitlement.AssignmentType, IsDirect = entitlement.AssignmentType == AssignmentType.Direct, IsInherited = entitlement.AssignmentType == AssignmentType.Group, IsPrivileged = principal.IsPrivileged, CalculatedAt = now };
                effective.Add(access);
                var path = new AccessPath { Id = Stable(provider, $"path-{index}"), PrincipalId = principal.Id, CloudAccountId = account.Id, TargetResourceId = resource.Id, PathType = entitlement.AssignmentType == AssignmentType.Group ? AccessPathType.GroupBased : AccessPathType.Direct, Description = "Synthetic demo access path", CreatedAt = now };
                paths.Add(path);
                pathNodes.AddRange(new[] { ("Principal", principal.Name, 0), ("Role", role.Name, 1), ("Permission", permission.Name, 2), ("Resource", resource.Name, 3) }.Select(node => new AccessPathNode { Id = Stable(provider, $"path-{index}-{node.Item3}"), AccessPathId = path.Id, NodeType = node.Item1, NodeId = node.Item3 == 0 ? principal.Id : node.Item3 == 1 ? role.Id : node.Item3 == 2 ? permission.Id : resource.Id, Name = node.Item2, Sequence = node.Item3 }));
                activity.Add(new ActivityRecord { Id = Stable(provider, $"activity-{index}"), PrincipalId = principal.Id, CloudAccountId = account.Id, ResourceId = resource.Id, Action = index == 2 ? "Dormant identity" : "ResourceRead", Service = name, Timestamp = principal.LastActivityAt ?? now, Source = "Synthetic Demo Mode", Success = true });
                var finding = new RiskFindingModel { Id = Stable(provider, $"finding-{index}"), RuleId = Stable(provider, $"rule-{index % 3}"), CloudProvider = provider == CloudProviderType.Azure ? CloudProvider.Azure : provider == CloudProviderType.Aws ? CloudProvider.Aws : CloudProvider.Gcp, CloudAccountId = account.Id, PrincipalId = principal.Id, ResourceId = resource.Id, EffectiveAccessId = access.Id, Severity = index == 0 ? RiskSeverity.Critical : index == 2 ? RiskSeverity.High : RiskSeverity.Medium, Title = index == 2 ? "External privileged identity" : index == 3 ? "Potentially excessive service permissions" : "Privileged identity", Description = "Synthetic evidence-backed Demo Mode finding.", Evidence = new RiskEvidence($"Principal={principal.Email}; Role={role.Name}; Scope=demo-production; Assignment={entitlement.AssignmentType}; Source=Synthetic Demo Mode"), Recommendation = "Review and reduce the synthetic privilege to the least-privilege role.", CreatedAt = now.AddDays(-index), UpdatedAt = now };
                findings.Add(finding);
                recommendations.Add(new RecommendationModel { Id = Stable(provider, $"recommendation-{index}"), RiskFindingId = finding.Id, Title = "Reduce demonstration privilege", Description = "Synthetic Demo Mode recommendation.", CurrentState = $"{role.Name} at demo-production", SuggestedState = "Narrow resource-scoped read access", Reason = "Reduce exposure and align with least privilege.", Priority = index + 1, CreatedAt = now });
            }
            scans.Add(new Scan { Id = Stable(provider, "scan"), CloudAccountId = account.Id, StartedAt = now.AddMinutes(-12), CompletedAt = now.AddMinutes(-8), Status = ScanStatus.Completed, ItemsDiscovered = 13, IdentitiesDiscovered = 5, PermissionsDiscovered = 1, FindingsGenerated = 4 });
        }

        return new GeneratedDemoData(accounts, principals, groups, groupMemberships, resources, roles, policies, permissions, entitlements, effective, paths, pathNodes, activity, findings, recommendations, scans);
    }

    private static Guid Stable(CloudProviderType provider, string key) => GuidUtility.Create(GuidUtility.Namespace, $"ciem-demo-{provider}-{key}");

    private sealed record GeneratedDemoData(IReadOnlyList<CloudAccount> Accounts, IReadOnlyList<CloudPrincipal> Principals, IReadOnlyList<CloudGroup> Groups, IReadOnlyList<GroupMembership> GroupMemberships, IReadOnlyList<CloudResource> Resources, IReadOnlyList<CloudRole> Roles, IReadOnlyList<CloudPolicy> Policies, IReadOnlyList<CloudPermission> Permissions, IReadOnlyList<Entitlement> Entitlements, IReadOnlyList<EffectiveAccess> EffectiveAccess, IReadOnlyList<AccessPath> AccessPaths, IReadOnlyList<AccessPathNode> AccessPathNodes, IReadOnlyList<ActivityRecord> ActivityRecords, IReadOnlyList<RiskFindingModel> RiskFindings, IReadOnlyList<RecommendationModel> Recommendations, IReadOnlyList<Scan> Scans);
}

internal static class GuidUtility
{
    public static readonly Guid Namespace = Guid.Parse("7c31cbf1-3b9f-4cc2-b9d2-0f3f0f02c6af");

    public static Guid Create(Guid namespaceId, string name)
    {
        var namespaceBytes = namespaceId.ToByteArray();
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        using var sha = System.Security.Cryptography.SHA1.Create();
        var hash = sha.ComputeHash(namespaceBytes.Concat(nameBytes).ToArray());
        var bytes = hash.Take(16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }
}
