using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class SubscriptionAccessTests
{
    [Theory]
    [InlineData("Trial", true)]
    [InlineData("Licensed", true)]
    [InlineData("Expired", false)]
    [InlineData("NotRegistered", false)]
    public async Task CloudCommands_RequireActiveServerEntitlement(string status, bool allowed)
    {
        var handler = new EntitlementHandler(status);
        var access = new SubscriptionAccessService(new HttpClient(handler) { BaseAddress = new("https://server.invalid/") }, new Tokens());
        var inner = new Runner();
        var runner = new LicensedCloudCommandRunner(inner, access);
        if (allowed) await runner.RunAsync("az", ["role", "assignment", "create"]);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync("az", ["role", "assignment", "create"]));
        Assert.Equal(allowed ? 1 : 0, inner.Calls);
        Assert.Equal("Bearer", handler.Authorization);
        Assert.Equal(HttpMethod.Get, handler.Method);
    }

    [Fact]
    public async Task Failure_BlocksNewScansButKeepsSessionDisconnectionAvailable()
    {
        var access = new SubscriptionAccessService(new HttpClient(new UnavailableHandler()) { BaseAddress = new("https://server.invalid/") }, new Tokens());
        var inner = new Runner();
        var runner = new LicensedCloudCommandRunner(inner, access);
        await Assert.ThrowsAsync<HttpRequestException>(() => runner.RunAsync("aws", ["s3api", "list-buckets"]));
        Assert.Equal(0, inner.Calls);
        await runner.RunAsync("aws", ["sso", "logout"]);
        Assert.Equal(1, inner.Calls);
        var scan = new LiveCloudScanService([], subscription: access);
        await Assert.ThrowsAsync<HttpRequestException>(() => scan.ScanAsync());
        Assert.Contains("read-only", access.Status);
    }

    [Fact]
    public async Task ExplicitRegistration_UsesTrialEndpoint()
    {
        var handler = new EntitlementHandler("Trial");
        var access = new SubscriptionAccessService(new HttpClient(handler) { BaseAddress = new("https://server.invalid/") }, new Tokens());
        await access.RefreshAsync(true);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/api/v1/subscription/trial", handler.Path);
    }

    [Theory]
    [InlineData(CloudProviderType.Azure)]
    [InlineData(CloudProviderType.Aws)]
    [InlineData(CloudProviderType.Gcp)]
    public async Task CloudSignInChecks_DoNotRequireLicenseServer(CloudProviderType provider)
    {
        var handler = new UnavailableHandler();
        var subscription = new SubscriptionAccessService(new HttpClient(handler) { BaseAddress = new("https://server.invalid/") }, new Tokens());
        var inner = new SessionRunner();
        var launcher = new CloudAuthenticationLauncher(new LicensedCloudCommandRunner(inner, subscription));

        Assert.True(await launcher.IsConnectedAsync(provider));
        Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain("unavailable", subscription.Status);
        if (provider == CloudProviderType.Aws)
            Assert.Contains(inner.Calls, args => args.Take(4).SequenceEqual(new[] { "--profile", "yask-ciem", "sts", "get-caller-identity" }));
    }

    [Theory]
    [InlineData("az", "role", "assignment", "create")]
    [InlineData("az", "account", "management-group", "list")]
    [InlineData("aws", "--profile", "yask-ciem", "iam", "create-role")]
    [InlineData("aws", "--profile", "yask-ciem", "s3api", "list-buckets")]
    [InlineData("aws", "sts", "get-caller-identity-extra")]
    [InlineData("gcloud", "auth", "list-extra")]
    [InlineData("gcloud", "projects", "list")]
    [InlineData("gcloud", "projects", "add-iam-policy-binding")]
    public async Task NonSessionCommands_StillRequireLicense(string command, params string[] arguments)
    {
        var access = new SubscriptionAccessService(new HttpClient(new UnavailableHandler()) { BaseAddress = new("https://server.invalid/") }, new Tokens());
        var inner = new Runner();
        var runner = new LicensedCloudCommandRunner(inner, access);

        await Assert.ThrowsAsync<HttpRequestException>(() => runner.RunAsync(command, arguments));

        Assert.Equal(0, inner.Calls);
    }

    [Theory]
    [InlineData(CloudProviderType.Azure)]
    [InlineData(CloudProviderType.Aws)]
    [InlineData(CloudProviderType.Gcp)]
    public async Task Disconnection_RemainsAvailableWithoutLicense(CloudProviderType provider)
    {
        var handler = new UnavailableHandler();
        var access = new SubscriptionAccessService(new HttpClient(handler) { BaseAddress = new("https://server.invalid/") }, new Tokens());
        var inner = new Runner();
        var launcher = new CloudAuthenticationLauncher(new LicensedCloudCommandRunner(inner, access));

        await launcher.DisconnectAsync(provider);

        Assert.Equal(1, inner.Calls);
        Assert.Equal(0, handler.Calls);
    }

    private sealed class SessionRunner : ICloudCommandRunner
    {
        public List<string[]> Calls { get; } = new();
        public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            var result = command switch
            {
                "az" => new CloudCommandResult(0, """[{"id":"subscription-a","state":"Enabled"}]""", ""),
                "gcloud" => new CloudCommandResult(0, """[{"account":"user@example.invalid","status":"ACTIVE"}]""", ""),
                "aws" when arguments[0] == "configure" => new CloudCommandResult(0, "yask-ciem", ""),
                "aws" when arguments[0] == "--profile" => new CloudCommandResult(0, """{"Account":"123456789012"}""", ""),
                "aws" => new CloudCommandResult(1, "", "No default credentials"),
                _ => throw new InvalidOperationException("Unexpected test command.")
            };
            return Task.FromResult(result);
        }
    }

    private sealed class Tokens : IApiAccessTokenProvider
    {
        public Task<string> GetApiAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("test-token");
    }
    private sealed class Runner : ICloudCommandRunner
    {
        public int Calls { get; private set; }
        public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new CloudCommandResult(0, "{}", ""));
        }
    }
    private sealed class EntitlementHandler(string status) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.Scheme;
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SubscriptionStatus(Guid.NewGuid(), status, now, now, now.AddDays(30),
                    status == "Licensed" ? now.AddDays(365) : null, status is "Trial" or "Licensed"))
            });
        }
    }
    private sealed class UnavailableHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
