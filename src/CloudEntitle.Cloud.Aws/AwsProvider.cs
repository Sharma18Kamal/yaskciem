namespace CloudEntitle.Cloud.Aws;

using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;

public class AwsProvider : ICloudProvider, IIdentityProvider, IResourceProvider, IRoleProvider, IPermissionProvider
{
    public CloudProviderType ProviderType => CloudProviderType.Aws;

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CloudAccount>>(new[]
        {
            new CloudAccount
            {
                Id = Guid.NewGuid(),
                Name = "AWS Demo Account",
                CloudProvider = CloudProviderType.Aws,
                ExternalAccountId = "123456789012",
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
                CloudProvider = CloudProviderType.Aws,
                Name = "admin-user",
                IdentityType = IdentityType.User,
                Email = "admin@example.com",
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
                Name = "AdministratorAccess",
                Action = "*",
                Resource = "*",
                Scope = "account:123456789012",
                Source = "AWS IAM Managed Policy"
            }
        });

    public Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CloudRole>>(new[]
        {
            new CloudRole
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Name = "AdministratorAccess",
                RoleId = "arn:aws:iam::123456789012:role/Admin",
                Description = "Full administrative access",
                Scope = "account:123456789012"
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
                Name = "iam-role-admin",
                Type = "AWS::IAM::Role",
                ResourceId = "arn:aws:iam::123456789012:role/Admin",
                Scope = "account:123456789012"
            }
        });

    public Task<IReadOnlyList<CloudRole>> GetRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
        => DiscoverRolesAsync(accountId, cancellationToken);
}
