using CloudEntitle.Application.Interfaces;
using CloudEntitle.Application.Services;
using CloudEntitle.Domain;
using Xunit;

namespace CloudEntitle.Application.Tests;

public class ApplicationServiceTests
{
    [Fact]
    public async Task ScanService_ShouldReturnFindings_WhenAzureProviderExists()
    {
        var providers = new ICloudProvider[]
        {
            new FakeAzureProvider()
        };

        var service = new ScanService(providers);

        var results = await service.RunScanAsync(Guid.NewGuid());

        Assert.NotEmpty(results);
        Assert.Contains(results, x => x.CloudProvider == CloudProviderType.Azure);
    }

    private sealed class FakeAzureProvider : ICloudProvider
    {
        public CloudProviderType ProviderType => CloudProviderType.Azure;

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudAccount>>(new[] { new CloudAccount { Id = Guid.NewGuid(), Name = "Azure Test", CloudProvider = CloudProviderType.Azure } });

        public Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudPrincipal>>(new List<CloudPrincipal>());

        public Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudRole>>(new List<CloudRole>());

        public Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudPermission>>(new List<CloudPermission>());
    }
}
