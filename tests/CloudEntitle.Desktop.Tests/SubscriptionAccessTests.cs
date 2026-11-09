using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
