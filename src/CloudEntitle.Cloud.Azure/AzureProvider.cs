namespace CloudEntitle.Cloud.Azure;

using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;

public class AzureProvider : ICloudProvider, IIdentityProvider, IResourceProvider, IRoleProvider, IPermissionProvider
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

    public Task<IReadOnlyList<CloudPrincipal>> GetPrincipalsAsync(Guid accountId, CancellationToken cancellationToken = default)
        => DiscoverIdentitiesAsync(accountId, cancellationToken);

    public Task<IReadOnlyList<CloudPermission>> GetPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
        => DiscoverPermissionsAsync(accountId, cancellationToken);

    public Task<IReadOnlyList<CloudResource>> GetResourcesAsync(Guid accountId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CloudResource>>(new[]
        {
            new CloudResource
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Name = "rg-production",
                Type = "Microsoft.Resources/resourceGroups",
                ResourceId = "/subscriptions/sub-demo-azure/resourceGroups/rg-production",
                Scope = "/subscriptions/sub-demo-azure"
            }
        });

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
        => _commandRunner!.RunAsync("az", arguments, cancellationToken);

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
