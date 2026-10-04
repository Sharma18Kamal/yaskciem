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
