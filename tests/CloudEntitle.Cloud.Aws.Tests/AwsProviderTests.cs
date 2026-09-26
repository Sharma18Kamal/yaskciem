using CloudEntitle.Cloud.Aws;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using Xunit;

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

        [Fact]
        public async Task AwsProvider_ShouldNormalizeReadOnlyScanAndTrustRelationships()
        {
            var provider = new AwsProvider(new FakeAwsCommandRunner(), useLiveDiscovery: true);

            var result = await provider.ScanAsync();

            Assert.Equal(ScanStatus.Completed, result.Scan.Status);
            Assert.NotEmpty(result.Accounts);
            Assert.NotEmpty(result.Principals);
            Assert.NotEmpty(result.Groups);
            Assert.NotEmpty(result.Policies);
            Assert.NotEmpty(result.Permissions);
            Assert.NotEmpty(result.TrustRelationships);
        }

        [Fact]
        public async Task AwsProvider_ShouldHonorCancellation()
        {
            var provider = new AwsProvider(new FakeAwsCommandRunner(), useLiveDiscovery: true);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var result = await provider.ScanAsync(cancellationToken: cancellation.Token);

            Assert.Equal(ScanStatus.Cancelled, result.Scan.Status);
        }

        private sealed class FakeAwsCommandRunner : ICloudCommandRunner
        {
            public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
            {
                var output = arguments.Contains("sts")
                    ? "{\"Account\":\"123456789012\",\"Arn\":\"arn:aws:iam::123456789012:user/demo\"}"
                    : arguments.Contains("list-users")
                        ? "{\"Users\":[{\"UserName\":\"demo-user\",\"UserId\":\"AIDEMO\"}]}"
                        : arguments.Contains("list-groups")
                            ? "{\"Groups\":[{\"GroupName\":\"demo-group\",\"GroupId\":\"AGDEMO\"}]}"
                            : arguments.Contains("list-roles")
                                ? "{\"Roles\":[{\"RoleName\":\"DemoRole\",\"Arn\":\"arn:aws:iam::123456789012:role/DemoRole\"}]}"
                                : arguments.Contains("list-policies")
                                    ? "{\"Policies\":[{\"PolicyName\":\"DemoPolicy\",\"Arn\":\"arn:aws:iam::123456789012:policy/DemoPolicy\",\"Document\":\"{\\\"Statement\\\":[{\\\"Effect\\\":\\\"Allow\\\",\\\"Action\\\":\\\"s3:*\\\",\\\"Resource\\\":\\\"arn:aws:s3:::demo/*\\\"}]}\"}]}"
                                    : "{\"Role\":{\"AssumeRolePolicyDocument\":\"{\\\"Statement\\\":[{\\\"Effect\\\":\\\"Allow\\\",\\\"Principal\\\":{\\\"AWS\\\":\\\"arn:aws:iam::123456789012:user/demo-user\\\"}}]}\"}}";
                return Task.FromResult(new CloudCommandResult(0, output, string.Empty));
            }
        }
}
