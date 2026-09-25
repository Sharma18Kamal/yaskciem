namespace CloudEntitle.Cloud.Gcp;

using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;

public class GcpProvider : ICloudProvider, IIdentityProvider, IResourceProvider, IRoleProvider, IPermissionProvider
{
    public CloudProviderType ProviderType => CloudProviderType.Gcp;

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CloudAccount>>(new[]
        {
            new CloudAccount
            {
                Id = Guid.NewGuid(),
                Name = "GCP Demo Project",
                CloudProvider = CloudProviderType.Gcp,
                ProjectId = "demo-project-123",
                ExternalAccountId = "gcp-demo",
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
                CloudProvider = CloudProviderType.Gcp,
                Name = "project-owner",
                IdentityType = IdentityType.ServiceAccount,
                Email = "project-owner@demo-project.iam.gserviceaccount.com",
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
                Action = "roles/owner",
                Resource = "projects/demo-project-123",
                Scope = "projects/demo-project-123",
                Source = "IAM Policy Binding"
            }
        });

    public Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CloudRole>>(new[]
        {
            new CloudRole
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Name = "roles/owner",
                RoleId = "roles/owner",
                Description = "Full access to the project",
                Scope = "projects/demo-project-123"
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
                Name = "demo-project-123",
                Type = "gcp-project",
                ResourceId = "projects/demo-project-123",
                Scope = "projects/demo-project-123"
            }
        });

    public Task<IReadOnlyList<CloudRole>> GetRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
        => DiscoverRolesAsync(accountId, cancellationToken);
}
