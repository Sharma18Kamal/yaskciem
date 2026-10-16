using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class CloudAuthenticationLauncherTests
{
    [Fact]
    public async Task AzureConnectionCheck_ShouldAcceptEnabledSubscriptionsWithoutCurrentAccountShow()
    {
        var runner = new FixedCommandRunner(new CloudCommandResult(0,
            "[{\"id\":\"subscription-a\",\"state\":\"Enabled\"},{\"id\":\"subscription-b\",\"state\":\"Enabled\"}]", string.Empty));
        var launcher = new CloudAuthenticationLauncher(runner);

        var connected = await launcher.IsConnectedAsync(CloudProviderType.Azure);

        Assert.True(connected);
        Assert.Equal(new[] { "account", "list", "--all", "--output", "json" }, runner.Arguments);
    }

    [Fact]
    public async Task AzureConnectionCheck_ShouldIgnoreDisabledOnlySubscriptions()
    {
        var runner = new FixedCommandRunner(new CloudCommandResult(0,
            "[{\"id\":\"subscription-disabled\",\"state\":\"Disabled\"}]", string.Empty));
        var launcher = new CloudAuthenticationLauncher(runner);

        Assert.False(await launcher.IsConnectedAsync(CloudProviderType.Azure));
    }

    [Fact]
    public async Task AwsConnectionCheck_ShouldAcceptDefaultCredentialsWithoutListingProfiles()
    {
        var runner = new ScriptedCommandRunner(new CloudCommandResult(0, "{}", string.Empty));
        var launcher = new CloudAuthenticationLauncher(runner);

        Assert.True(await launcher.IsConnectedAsync(CloudProviderType.Aws));
        Assert.Equal(new[] { "sts", "get-caller-identity", "--output", "json" }, Assert.Single(runner.Calls));
    }

    [Fact]
    public async Task AwsConnectionCheck_ShouldAcceptNamedProfileAfterExpiredProfile()
    {
        var runner = new ScriptedCommandRunner(
            new CloudCommandResult(1, string.Empty, "Unable to locate credentials"),
            new CloudCommandResult(0, "expired\r\n yask-audit \r\nyask-audit\r\n", string.Empty),
            new CloudCommandResult(1, string.Empty, "SSO session expired"),
            new CloudCommandResult(0, "{\"Account\":\"123456789012\"}", string.Empty));
        var launcher = new CloudAuthenticationLauncher(runner);

        Assert.True(await launcher.IsConnectedAsync(CloudProviderType.Aws));
        Assert.Equal(new[] { "configure", "list-profiles" }, runner.Calls[1]);
        Assert.Equal(new[] { "--profile", "expired", "sts", "get-caller-identity", "--output", "json" }, runner.Calls[2]);
        Assert.Equal(new[] { "--profile", "yask-audit", "sts", "get-caller-identity", "--output", "json" }, runner.Calls[3]);
        Assert.Equal(4, runner.Calls.Count);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(0, "\r\n")]
    public async Task AwsConnectionCheck_ShouldRejectUnavailableProfiles(int exitCode, string profiles)
    {
        var runner = new ScriptedCommandRunner(
            new CloudCommandResult(1, string.Empty, "Unable to locate credentials"),
            new CloudCommandResult(exitCode, profiles, string.Empty));
        var launcher = new CloudAuthenticationLauncher(runner);

        Assert.False(await launcher.IsConnectedAsync(CloudProviderType.Aws));
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task AwsConnectionCheck_ShouldRejectExpiredProfilesAndDeduplicateNames()
    {
        var runner = new ScriptedCommandRunner(
            new CloudCommandResult(1, string.Empty, "Unable to locate credentials"),
            new CloudCommandResult(0, "yask-audit\nyask-audit\n", string.Empty),
            new CloudCommandResult(1, string.Empty, "SSO session expired"));
        var launcher = new CloudAuthenticationLauncher(runner);

        Assert.False(await launcher.IsConnectedAsync(CloudProviderType.Aws));
        Assert.Equal(3, runner.Calls.Count);
    }

    private sealed class ScriptedCommandRunner(params CloudCommandResult[] results) : ICloudCommandRunner
    {
        private readonly Queue<CloudCommandResult> _results = new(results);
        public List<string[]> Calls { get; } = new();

        public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("aws", command);
            Calls.Add(arguments.ToArray());
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class FixedCommandRunner(CloudCommandResult result) : ICloudCommandRunner
    {
        public string? Command { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();

        public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Command = command;
            Arguments = arguments.ToArray();
            return Task.FromResult(result);
        }
    }
}
