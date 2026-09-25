using CloudEntitle.Cloud.Aws;
using CloudEntitle.Domain;

namespace CloudEntitle.Cloud.Aws.Tests;

public class AwsProviderTests
{
    [Fact]
    public async Task AwsProvider_ShouldDiscoverConnectedAccount()
    {
        var provider = new AwsProvider();

        var accounts = await provider.DiscoverAccountsAsync();

        Assert.NotEmpty(accounts);
        Assert.Equal(CloudProviderType.Aws, accounts[0].CloudProvider);
        Assert.True(accounts[0].IsReadOnly);
    }

    [Fact]
    public async Task AwsProvider_ShouldExposePrivilegedRole()
    {
        var provider = new AwsProvider();

        var roles = await provider.DiscoverRolesAsync(Guid.NewGuid());

        Assert.NotEmpty(roles);
        Assert.Equal("AdministratorAccess", roles[0].Name);
    }
}
