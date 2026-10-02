namespace CloudEntitle.Cloud.Azure;

using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Cloud.Azure.Services;
using CloudEntitle.Cloud.Azure.Mappers;

public class AzureProvider : ICloudProvider, IIdentityProvider, IResourceProvider, IRoleProvider, IPermissionProvider, ICloudEffectiveAccessProvider
{
    private readonly ICloudCommandRunner? _commandRunner;
    private readonly bool _useLiveDiscovery;
    private readonly Dictionary<Guid, CloudAccount> _accounts = new();

    public AzureProvider(ICloudCommandRunner? commandRunner = null, bool useLiveDiscovery = false)
    {
        _commandRunner = commandRunner;
        _useLiveDiscovery = useLiveDiscovery && commandRunner is not null;
    }

    public CloudProviderType ProviderType => CloudProviderType.Azure;

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return true;
        }

        var result = await RunAzAsync(new[] { "account", "show", "--output", "json" }, cancellationToken);
        return result.IsSuccess;
    }

    public async Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return MockAccounts();
        }

        using var document = JsonDocument.Parse(await GetAzOutputAsync(
            new[] { "account", "show", "--output", "json" }, cancellationToken));
        var root = document.RootElement;
        var account = new CloudAccount
        {
            Id = Guid.NewGuid(),
            Name = root.GetProperty("name").GetString() ?? "Azure subscription",
            CloudProvider = CloudProviderType.Azure,
            SubscriptionId = root.GetProperty("id").GetString(),
            ExternalAccountId = root.TryGetProperty("tenantId", out var tenant) ? tenant.GetString() : null,
            TenantId = root.TryGetProperty("tenantId", out tenant) ? tenant.GetString() : null,
            ConnectionStatus = ConnectionStatus.Connected,
            IsReadOnly = true,
            LastScanAt = DateTime.UtcNow
        };
        _accounts[account.Id] = account;
        return new[] { account };
    }

    public async Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return MockIdentities(accountId);
        }

        return (await GetAssignmentsAsync(accountId, cancellationToken))
            .Where(assignment => assignment.TryGetProperty("principalId", out _))
            .GroupBy(assignment => assignment.GetProperty("principalId").GetString())
            .Select(group =>
            {
                var assignment = group.First();
                var principalType = assignment.TryGetProperty("principalType", out var type)
                    ? type.GetString()
                    : null;
                return new CloudPrincipal
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    CloudProvider = CloudProviderType.Azure,
                    Name = assignment.TryGetProperty("principalName", out var name)
                        ? name.GetString() ?? group.Key ?? "unknown-principal"
                        : group.Key ?? "unknown-principal",
                    PrincipalId = group.Key,
                    IdentityType = principalType?.Equals("Group", StringComparison.OrdinalIgnoreCase) == true
                        ? IdentityType.Group
                        : principalType?.Contains("Service", StringComparison.OrdinalIgnoreCase) == true
                            ? IdentityType.ServiceAccount
                            : IdentityType.User,
                    IsPrivileged = group.Any(IsPrivilegedAssignment),
                    IsActive = true
                };
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return MockPermissions(accountId);
        }

        return (await GetAssignmentsAsync(accountId, cancellationToken))
            .Select(assignment => new CloudPermission
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Name = assignment.TryGetProperty("roleDefinitionName", out var role)
                    ? role.GetString() ?? "Unknown role"
                    : "Unknown role",
                Action = assignment.TryGetProperty("roleDefinitionName", out role) ? role.GetString() : null,
                Resource = assignment.TryGetProperty("scope", out var scope) ? scope.GetString() : null,
                Scope = assignment.TryGetProperty("scope", out scope) ? scope.GetString() : null,
                Source = "Azure RBAC"
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return MockRoles(accountId);
        }

        return (await GetAssignmentsAsync(accountId, cancellationToken))
            .Where(assignment => assignment.TryGetProperty("roleDefinitionId", out _))
            .GroupBy(assignment => assignment.GetProperty("roleDefinitionId").GetString())
            .Select(group =>
            {
                var assignment = group.First();
                return new CloudRole
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    Name = assignment.TryGetProperty("roleDefinitionName", out var name)
                        ? name.GetString() ?? "Unknown role"
                        : "Unknown role",
                    RoleId = group.Key ?? string.Empty,
                    Scope = assignment.TryGetProperty("scope", out var scope) ? scope.GetString() : null
                };
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<CloudEffectiveAccessAssignment>> DiscoverEffectiveAccessAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return Array.Empty<CloudEffectiveAccessAssignment>();
        }

        return (await GetAssignmentsAsync(accountId, cancellationToken))
            .Where(assignment => assignment.TryGetProperty("principalId", out _))
            .Select(assignment =>
            {
                var roleName = assignment.TryGetProperty("roleDefinitionName", out var role) ? role.GetString() ?? "Unknown role" : "Unknown role";
                var principal = assignment.TryGetProperty("principalName", out var name) ? name.GetString() : null;
                if (string.IsNullOrWhiteSpace(principal) && assignment.TryGetProperty("principalId", out var principalId))
                {
                    principal = principalId.GetString();
                }
                var scope = assignment.TryGetProperty("scope", out var scopeValue) ? scopeValue.GetString() ?? "Subscription" : "Subscription";
                return new CloudEffectiveAccessAssignment(
                    principal ?? "Unknown principal",
                    roleName,
                    scope,
                    "Azure RBAC role assignment",
                    roleName.Contains("Owner", StringComparison.OrdinalIgnoreCase) || roleName.Contains("Administrator", StringComparison.OrdinalIgnoreCase));
            })
            .ToArray();
    }

    public async Task<(IReadOnlyList<CloudRole> Roles, IReadOnlyList<CloudPermission> Permissions)> DiscoverRoleDefinitionsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return (MockRoles(accountId), MockPermissions(accountId));
        }

        if (!_accounts.TryGetValue(accountId, out var account) || string.IsNullOrWhiteSpace(account.SubscriptionId))
        {
            throw new InvalidOperationException("The Azure account must be discovered before role definitions can be loaded.");
        }

        using var document = JsonDocument.Parse(await GetAzOutputAsync(new[] { "role", "definition", "list", "--subscription", account.SubscriptionId, "--output", "json" }, cancellationToken));
        var roles = new List<CloudRole>();
        var permissions = new List<CloudPermission>();
        foreach (var roleDefinition in document.RootElement.EnumerateArray())
        {
            var roleId = roleDefinition.TryGetProperty("name", out var name) ? name.GetString() : null;
            var properties = roleDefinition.TryGetProperty("properties", out var value) ? value : roleDefinition;
            var roleNameText = properties.TryGetProperty("roleName", out var roleName) ? roleName.GetString() ?? "Azure role" : "Azure role";
            var role = new CloudRole { Id = Guid.NewGuid(), AccountId = accountId, RoleId = roleId ?? string.Empty, Name = roleNameText, Description = properties.TryGetProperty("description", out var description) ? description.GetString() : null, IsPrivileged = roleNameText.Contains("Owner", StringComparison.OrdinalIgnoreCase) || roleNameText.Contains("Administrator", StringComparison.OrdinalIgnoreCase) };
            roles.Add(role);
            permissions.AddRange(AzurePermissionMapper.MapRolePermissions(accountId, roleDefinition));
        }

        return (roles, permissions);
    }

    public Task<IReadOnlyList<CloudPrincipal>> GetPrincipalsAsync(Guid accountId, CancellationToken cancellationToken = default)
        => DiscoverIdentitiesAsync(accountId, cancellationToken);

    public Task<IReadOnlyList<CloudPermission>> GetPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
        => DiscoverPermissionsAsync(accountId, cancellationToken);

    public async Task<IReadOnlyList<CloudResource>> GetResourcesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return new[] { new CloudResource { Id = Guid.NewGuid(), AccountId = accountId, Name = "rg-production", Type = "Microsoft.Resources/resourceGroups", ResourceId = "/subscriptions/sub-demo-azure/resourceGroups/rg-production", Scope = "/subscriptions/sub-demo-azure" } };
        }

        if (!_accounts.TryGetValue(accountId, out var account) || string.IsNullOrWhiteSpace(account.SubscriptionId))
        {
            throw new InvalidOperationException("The Azure account must be discovered before resources can be loaded.");
        }

        using var document = JsonDocument.Parse(await GetAzOutputAsync(new[] { "resource", "list", "--subscription", account.SubscriptionId, "--output", "json" }, cancellationToken));
        return document.RootElement.EnumerateArray().Select(resource => new CloudResource
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ExternalId = resource.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
            Name = resource.TryGetProperty("name", out var name) ? name.GetString() ?? "Azure resource" : "Azure resource",
            Type = resource.TryGetProperty("type", out var type) ? type.GetString() ?? "Unknown" : "Unknown",
            ResourceType = ResourceType.Resource,
            ResourceUri = resource.TryGetProperty("id", out id) ? id.GetString() : null,
            Region = resource.TryGetProperty("location", out var location) ? location.GetString() : null,
            Location = resource.TryGetProperty("location", out location) ? location.GetString() : null,
            Scope = resource.TryGetProperty("id", out id) ? id.GetString() : null
        }).ToArray();
    }

    public Task<IReadOnlyList<CloudRole>> GetRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
        => DiscoverRolesAsync(accountId, cancellationToken);

    private async Task<JsonElement[]> GetAssignmentsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (!_accounts.TryGetValue(accountId, out var account) || string.IsNullOrWhiteSpace(account.SubscriptionId))
        {
            throw new InvalidOperationException("The Azure account must be discovered before assignments can be loaded.");
        }

        using var document = JsonDocument.Parse(await GetAzOutputAsync(
            new[] { "role", "assignment", "list", "--all", "--subscription", account.SubscriptionId, "--output", "json" },
            cancellationToken));
        return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
    }

    private async Task<string> GetAzOutputAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunAzAsync(arguments, cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Azure CLI command failed: {result.StandardError}");
        }

        return result.StandardOutput;
    }

    private Task<CloudCommandResult> RunAzAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => RunAzWithRetryAsync(arguments, cancellationToken);

    public async Task<AzureScanResult> ScanAsync(Guid? accountId = null, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var accounts = new List<CloudAccount>();
        var resources = new List<CloudResource>();
        var principals = new List<CloudPrincipal>();
        var roles = new List<CloudRole>();
        var permissions = new List<CloudPermission>();
        var assignments = new List<RoleAssignment>();
        var entitlements = new List<Entitlement>();
        var errors = new List<string>();
        try
        {
            accounts.AddRange(await DiscoverAccountsAsync(cancellationToken));
            if (accountId.HasValue) accounts = accounts.Where(account => account.Id == accountId.Value).ToList();
            foreach (var account in accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    resources.AddRange(await GetResourcesAsync(account.Id, cancellationToken));
                    principals.AddRange(await DiscoverIdentitiesAsync(account.Id, cancellationToken));
                    roles.AddRange(await DiscoverRolesAsync(account.Id, cancellationToken));
                    permissions.AddRange(await DiscoverPermissionsAsync(account.Id, cancellationToken));
                    var discoveredAssignments = await GetRoleAssignmentsAsync(account, cancellationToken);
                    assignments.AddRange(discoveredAssignments.Select(item => new RoleAssignment { Id = Guid.NewGuid(), AccountId = account.Id, PrincipalId = principals.FirstOrDefault(principal => principal.PrincipalId == item.PrincipalId)?.Id ?? Guid.Empty, RoleId = roles.FirstOrDefault(role => role.RoleId == item.RoleId)?.Id ?? Guid.Empty, Scope = item.Scope, Source = "Azure RBAC" }));
                    entitlements.AddRange(assignments.Where(item => item.AccountId == account.Id && item.PrincipalId != Guid.Empty && item.RoleId != Guid.Empty).Select(item => new Entitlement { Id = Guid.NewGuid(), PrincipalId = item.PrincipalId, RoleId = item.RoleId, CloudAccountId = account.Id, Scope = item.Scope, AssignmentType = AssignmentType.Direct, GrantedAt = startedAt }));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors.Add($"{account.Name}: {exception.Message}");
                }
            }

            var scan = new Scan { Id = Guid.NewGuid(), CloudAccountId = accounts.FirstOrDefault()?.Id ?? Guid.Empty, StartedAt = startedAt, CompletedAt = DateTime.UtcNow, Status = errors.Count == accounts.Count && accounts.Count > 0 ? ScanStatus.Failed : ScanStatus.Completed, ItemsDiscovered = resources.Count, IdentitiesDiscovered = principals.Count, PermissionsDiscovered = permissions.Count, ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors) };
            return new AzureScanResult(scan, accounts, resources, principals, roles, permissions, assignments, entitlements, errors);
        }
        catch (OperationCanceledException)
        {
            return new AzureScanResult(new Scan { Id = Guid.NewGuid(), CloudAccountId = accounts.FirstOrDefault()?.Id ?? Guid.Empty, StartedAt = startedAt, CompletedAt = DateTime.UtcNow, Status = ScanStatus.Cancelled, ErrorMessage = "Azure scan cancelled." }, accounts, resources, principals, roles, permissions, assignments, entitlements, errors);
        }
    }

    private async Task<IReadOnlyList<RoleAssignmentRecord>> GetRoleAssignmentsAsync(CloudAccount account, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await GetAzOutputAsync(new[] { "role", "assignment", "list", "--all", "--subscription", account.SubscriptionId!, "--output", "json" }, cancellationToken));
        return document.RootElement.EnumerateArray().Select(item => new RoleAssignmentRecord(
            item.TryGetProperty("principalId", out var principal) ? principal.GetString() : null,
            item.TryGetProperty("roleDefinitionId", out var role) ? role.GetString() : null,
            item.TryGetProperty("scope", out var scope) ? scope.GetString() : null)).ToArray();
    }

    private async Task<CloudCommandResult> RunAzWithRetryAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        CloudCommandResult? result = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await _commandRunner!.RunAsync("az", arguments, cancellationToken);
            if (result.IsSuccess || attempt == 2) return result;
            await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)), cancellationToken);
        }

        return result!;
    }

    private sealed record RoleAssignmentRecord(string? PrincipalId, string? RoleId, string? Scope);

    private static bool IsPrivilegedAssignment(JsonElement assignment)
        => assignment.TryGetProperty("roleDefinitionName", out var role)
            && (role.GetString()?.Contains("Owner", StringComparison.OrdinalIgnoreCase) == true
                || role.GetString()?.Contains("Administrator", StringComparison.OrdinalIgnoreCase) == true);

    private static IReadOnlyList<CloudAccount> MockAccounts() => new[]
    {
        new CloudAccount
        {
            Id = Guid.NewGuid(), Name = "Azure Demo Subscription", CloudProvider = CloudProviderType.Azure,
            SubscriptionId = "sub-demo-azure", ExternalAccountId = "tenant-demo",
            ConnectionStatus = ConnectionStatus.Connected, IsReadOnly = true
        }
    };

    private static IReadOnlyList<CloudPrincipal> MockIdentities(Guid accountId) => new[]
    {
        new CloudPrincipal
        {
            Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Azure,
            Name = "Global Administrator", IdentityType = IdentityType.User, Email = "admin@contoso.com",
            IsPrivileged = true, IsActive = true
        }
    };

    private static IReadOnlyList<CloudPermission> MockPermissions(Guid accountId) => new[]
    {
        new CloudPermission
        {
            Id = Guid.NewGuid(), AccountId = accountId, Name = "Owner", Action = "*/*",
            Resource = "/subscriptions/sub-demo-azure", Scope = "/subscriptions/sub-demo-azure", Source = "Azure RBAC"
        }
    };

    private static IReadOnlyList<CloudRole> MockRoles(Guid accountId) => new[]
    {
        new CloudRole
        {
            Id = Guid.NewGuid(), AccountId = accountId, Name = "Owner",
            RoleId = "8e3af657-a8ff-443c-a75c-2eb6d3b5c6a9", Description = "Full access to all resources",
            Scope = "/subscriptions/sub-demo-azure"
        }
    };
}
