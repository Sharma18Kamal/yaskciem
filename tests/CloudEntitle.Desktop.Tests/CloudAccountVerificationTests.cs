using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class CloudAccountVerificationTests
{
    [Theory]
    [InlineData("approved-audit")]
    [InlineData("default")]
    public async Task Aws_ShouldUseOnlySelectedProfileAndReturnActualIdentity(string profile)
    {
        var runner = new Runner("""{"Account":"123456789012","Arn":"arn:aws:sts::123456789012:assumed-role/Audit/session"}""");
        var result = await CloudAccountVerifier.VerifyAsync(runner, Account("Aws", "123456789012", profile));
        Assert.Equal("123456789012", result.AccountId);
        Assert.Contains("assumed-role/Audit", result.Identity);
        Assert.Equal(["--profile", profile, "sts", "get-caller-identity", "--output", "json", "--no-cli-pager"], runner.Calls.Single());
        Assert.Equal(string.Empty, result.TenantId);
    }

    [Fact]
    public async Task Aws_ShouldRejectProfileThatNowResolvesToDifferentAccount()
    {
        var runner = new Runner("""{"Account":"999999999999","Arn":"arn:aws:iam::999999999999:user/other"}""");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CloudAccountVerifier.VerifyAsync(runner, Account("Aws", "123456789012", "changed-profile")));
        Assert.Contains("does not match", error.Message);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Azure_ShouldCheckLiveSubscriptionReadAndTenant()
    {
        var runner = new Runner(
            """{"id":"sub-a","tenantId":"tenant-a","state":"Enabled","user":{"name":"audit-identity"}}""",
            """{"subscriptionId":"sub-a","tenantId":"tenant-a"}""");
        var result = await CloudAccountVerifier.VerifyAsync(runner, Account("Azure", "sub-a"));
        Assert.Equal("audit-identity", result.Identity);
        Assert.Equal("tenant-a", result.TenantId);
        Assert.Equal("rest", runner.Calls[1][0]);
        Assert.Contains("get", runner.Calls[1]);
    }

    [Theory]
    [InlineData("sub-b", "tenant-a")]
    [InlineData("sub-a", "tenant-b")]
    public async Task Azure_ShouldRejectLiveScopeOrTenantMismatch(string subscription, string tenant)
    {
        var runner = new Runner(
            """{"id":"sub-a","tenantId":"tenant-a","state":"Enabled","user":{"name":"audit"}}""",
            $$"""{"subscriptionId":"{{subscription}}","tenantId":"{{tenant}}"}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => CloudAccountVerifier.VerifyAsync(runner, Account("Azure", "sub-a")));
    }

    [Fact]
    public async Task Gcp_ShouldPinProjectReadToDisplayedIdentity()
    {
        var runner = new Runner("{}", """[{"account":"audit@example.test"}]""", """{"projectId":"approved-project"}""");
        var result = await CloudAccountVerifier.VerifyAsync(runner, Account("Gcp", "approved-project"));
        Assert.Equal("audit@example.test", result.Identity);
        Assert.Equal(["projects", "describe", "approved-project", "--account", "audit@example.test", "--format=json"], runner.Calls[2]);
    }

    [Fact]
    public async Task Gcp_ShouldRejectImpersonationRatherThanMisidentifyEffectiveCaller()
    {
        var runner = new Runner("""{"auth":{"impersonate_service_account":"service@example.test"}}""");
        await Assert.ThrowsAsync<NotSupportedException>(() => CloudAccountVerifier.VerifyAsync(runner, Account("Gcp", "approved-project")));
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("""{"Account":"123456789012"}""")]
    public async Task ShouldRejectIncompleteOrMalformedIdentity(string response)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CloudAccountVerifier.VerifyAsync(new Runner(response), Account("Aws", "123456789012")));
    }

    [Fact]
    public async Task ShouldReportDeniedWithoutLeakingProviderResponse()
    {
        var runner = new Runner { Failure = new(1, "sensitive-response", "sensitive-error") };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CloudAccountVerifier.VerifyAsync(runner, Account("Aws", "123456789012")));
        Assert.Contains("read permissions", error.Message);
        Assert.DoesNotContain("sensitive", error.Message);
    }

    [Fact]
    public async Task ShouldHonorCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CloudAccountVerifier.VerifyAsync(new Runner(), Account("Aws", "123456789012"), cancellation.Token));
    }

    private static LiveScanAccountRecord Account(string provider, string id, string profile = "")
        => new(provider, "Approved account", id, profile, "Read only");

    private sealed class Runner(params string[] responses) : ICloudCommandRunner
    {
        public List<string[]> Calls { get; } = [];
        public CloudCommandResult? Failure { get; init; }
        public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(arguments.ToArray());
            return Task.FromResult(Failure ?? new CloudCommandResult(0, responses[Calls.Count - 1], ""));
        }
    }
}
