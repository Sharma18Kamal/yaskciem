using CloudEntitle.Cloud.Gcp;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using Xunit;

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

    [Fact]
    public async Task GcpProvider_ShouldNormalizeReadOnlyScan()
    {
        var provider = new GcpProvider(new FakeGcloudRunner(), useLiveDiscovery: true);

        var result = await provider.ScanAsync();

        Assert.Equal(ScanStatus.Completed, result.Scan.Status);
        Assert.NotEmpty(result.Accounts);
        Assert.NotEmpty(result.Resources);
        Assert.NotEmpty(result.Principals);
        Assert.NotEmpty(result.Groups);
        Assert.NotEmpty(result.Policies);
        Assert.NotEmpty(result.Permissions);
        Assert.Contains(result.Bindings, binding => binding.ConditionExpression != null);
    }

    [Fact]
    public async Task GcpProvider_ShouldHonorCancellation()
    {
        var provider = new GcpProvider(new FakeGcloudRunner(), useLiveDiscovery: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await provider.ScanAsync(cancellationToken: cancellation.Token);

        Assert.Equal(ScanStatus.Cancelled, result.Scan.Status);
    }

    private sealed class FakeGcloudRunner : ICloudCommandRunner
    {
        public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            var output = arguments.Contains("projects") && arguments.Contains("list")
                ? "[{\"projectId\":\"demo-production\",\"name\":\"Demo Production\",\"projectNumber\":\"123\"}]"
                : arguments.Contains("service-accounts")
                    ? "[{\"email\":\"demo-sa@demo-production.iam.gserviceaccount.com\",\"uniqueId\":\"sa-123\",\"displayName\":\"Demo Service Account\",\"disabled\":false}]"
                    : "{\"bindings\":[{\"role\":\"roles/owner\",\"members\":[\"user:demo@example.invalid\",\"group:demo@example.invalid\",\"serviceAccount:demo-sa@demo-production.iam.gserviceaccount.com\"],\"condition\":{\"title\":\"DemoCondition\",\"expression\":\"resource.name.startsWith('projects/demo-production')\"}}]}";
            return Task.FromResult(new CloudCommandResult(0, output, string.Empty));
        }
    }
}
