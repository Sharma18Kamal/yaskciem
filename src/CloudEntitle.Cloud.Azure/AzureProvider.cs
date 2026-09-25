namespace CloudEntitle.Cloud.Azure;

using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;

public class AzureProvider : ICloudProvider, IIdentityProvider, IResourceProvider, IRoleProvider, IPermissionProvider
{
    public CloudProviderType ProviderType => CloudProviderType.Azure;

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CloudAccount>>(new[]
        {
            new CloudAccount
            {
                Id = Guid.NewGuid(),
                Name = "Azure Demo Subscription",
                CloudProvider = CloudProviderType.Azure,
                SubscriptionId = "sub-demo-azure",
                ExternalAccountId = "tenant-demo",
                ConnectionStatus = ConnectionStatus.Connected,
                IsReadOnly = true
            }
        });

    public Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CloudPrincipal>>(new[]
        {
            new CloudPrincipal
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                CloudProvider = CloudProviderType.Azure,
                Name = "Global Administrator",
                IdentityType = IdentityType.User,
                Email = "admin@contoso.com",
                IsPrivileged = true,
                IsActive = true
            }
        });

    public Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CloudPermission>>(new[]
        {
            new CloudPermission
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Name = "Owner",
                Action = "*/*",
                Resource = "/subscriptions/sub-demo-azure",
                Scope = "/subscriptions/sub-demo-azure",
                Source = "Azure RBAC"
            }
        });

    public Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CloudRole>>(new[]
        {
            new CloudRole
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Name = "Owner",
                RoleId = "8e3af657-a8ff-443c-a75c-2eb6d3b5c6a9",
                Description = "Full access to all resources",
                Scope = "/subscriptions/sub-demo-azure"
            }
        });

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
}
