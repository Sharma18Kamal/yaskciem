using CloudEntitle.Cloud.Azure;
using CloudEntitle.Domain;

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
}
