using CloudEntitle.Desktop.Services;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class OrganizationSsoServiceTests
{
    [Fact]
    public async Task SignInWithoutConfiguredClientId_ShouldFailBeforeLaunchingBrowser()
    {
        var service = new EntraOrganizationSsoService(clientId: string.Empty);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SignInAsync(Guid.NewGuid().ToString()));

        Assert.Contains("YASK_CIEM_ENTRA_CLIENT_ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignInWithInvalidTenantId_ShouldFailBeforeLaunchingBrowser()
    {
        var service = new EntraOrganizationSsoService(clientId: Guid.NewGuid().ToString());

        await Assert.ThrowsAsync<ArgumentException>(() => service.SignInAsync("not-a-tenant-id"));
    }
}
