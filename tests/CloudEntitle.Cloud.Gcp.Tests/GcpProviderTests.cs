using CloudEntitle.Cloud.Gcp;
using CloudEntitle.Domain;

namespace CloudEntitle.Cloud.Gcp.Tests;

public class GcpProviderTests
{
    [Fact]
    public async Task GcpProvider_ShouldDiscoverConnectedAccount()
    {
        var provider = new GcpProvider();

        var accounts = await provider.DiscoverAccountsAsync();

        Assert.NotEmpty(accounts);
        Assert.Equal(CloudProviderType.Gcp, accounts[0].CloudProvider);
        Assert.True(accounts[0].IsReadOnly);
    }

    [Fact]
    public async Task GcpProvider_ShouldExposeOwnerRole()
    {
        var provider = new GcpProvider();

        var roles = await provider.DiscoverRolesAsync(Guid.NewGuid());

        Assert.NotEmpty(roles);
        Assert.Equal("roles/owner", roles[0].Name);
    }
}
