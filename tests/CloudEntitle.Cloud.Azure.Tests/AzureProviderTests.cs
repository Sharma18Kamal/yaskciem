using CloudEntitle.Cloud.Azure;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using Xunit;

namespace CloudEntitle.Cloud.Azure.Tests;

public class AzureProviderTests
{
    [Fact]
    public async Task AzureProvider_ShouldDiscoverConnectedAccount()
    {
        var provider = new AzureProvider();

        var accounts = await provider.DiscoverAccountsAsync();

        Assert.NotEmpty(accounts);
        Assert.Equal(CloudProviderType.Azure, accounts[0].CloudProvider);
        Assert.True(accounts[0].IsReadOnly);
    }

    [Fact]
    public async Task AzureProvider_ShouldExposePrivilegedIdentity()
    {
        var provider = new AzureProvider();

        var identities = await provider.DiscoverIdentitiesAsync(Guid.NewGuid());

        Assert.NotEmpty(identities);
        Assert.True(identities[0].IsPrivileged);
    }

    [Fact]
    public async Task AzureProvider_ShouldMapReadOnlyCliJson()
    {
        var runner = new FakeCommandRunner();
        var provider = new AzureProvider(runner, useLiveDiscovery: true);

        var accounts = await provider.DiscoverAccountsAsync();
        var permissions = await provider.DiscoverPermissionsAsync(accounts[0].Id);

        Assert.Equal("subscription-001", accounts[0].SubscriptionId);
        Assert.Equal("Owner", permissions[0].Name);
        Assert.Equal("/subscriptions/subscription-001", permissions[0].Scope);
    }

    private sealed class FakeCommandRunner : ICloudCommandRunner
    {
        public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            var output = arguments[0] == "account"
                ? "{\"name\":\"Production\",\"id\":\"subscription-001\",\"tenantId\":\"tenant-001\"}"
                : "[{\"principalId\":\"principal-001\",\"principalName\":\"owner@example.com\",\"principalType\":\"User\",\"roleDefinitionId\":\"role-001\",\"roleDefinitionName\":\"Owner\",\"scope\":\"/subscriptions/subscription-001\"}]";
            return Task.FromResult(new CloudCommandResult(0, output, string.Empty));
        }
    }
}
