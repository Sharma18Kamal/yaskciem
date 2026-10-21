using System.Net;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class JiraConnectorTests
{
    [Theory]
    [InlineData(201, true, false, null)]
    [InlineData(400, false, false, "http_rejected")]
    [InlineData(401, false, false, "http_rejected")]
    [InlineData(302, false, false, "http_rejected")]
    [InlineData(429, false, true, "http_rejected")]
    [InlineData(503, false, false, "outcome_unknown")]
    [InlineData(408, false, false, "outcome_unknown")]
    public async Task Jira_ShouldCreateMappedIssueWithoutReadingBodiesOrRetryingAmbiguousResults(int status, bool delivered, bool retry, string? failure)
    {
        var lease = Lease();
        using var handler = new JiraHandler(status);
        using var transport = new CentralWebhookTransport(Configuration(lease), handler);
        var result = await transport.SendAsync(lease, default);
        Assert.Equal(delivered, result.Delivered);
        Assert.Equal(retry, result.Retryable);
        Assert.Equal(failure, result.FailureCode);
        Assert.Equal("https://jira.invalid/rest/api/3/issue", handler.Uri!.AbsoluteUri);
        Assert.Equal("Basic", handler.AuthorizationScheme);
        Assert.Equal(lease.Delivery.DeliveryId.ToString("D"), handler.IdempotencyKey);
        using var json = JsonDocument.Parse(handler.Body);
        var fields = json.RootElement.GetProperty("fields");
        Assert.Equal("SEC", fields.GetProperty("project").GetProperty("key").GetString());
        Assert.Equal("Task", fields.GetProperty("issuetype").GetProperty("name").GetString());
        Assert.Contains($"yask-delivery-{lease.Delivery.DeliveryId:D}", fields.GetProperty("labels").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain("test-token-secret", handler.Body);
        Assert.DoesNotContain("response-secret", JsonSerializer.Serialize(result));
        Assert.Contains("scan.failed", handler.Body);
    }

    [Fact]
    public async Task Jira_ShouldRejectUnsafeConfigurationAndSanitizeAmbiguousNetworkFailures()
    {
        var lease = Lease();
        using var handler = new JiraHandler(201) { Fail = true };
        using var transport = new CentralWebhookTransport(Configuration(lease), handler);
        var result = await transport.SendAsync(lease, default);
        Assert.Equal("outcome_unknown", result.FailureCode);
        Assert.False(result.Retryable);
        Assert.DoesNotContain("test-token-secret", JsonSerializer.Serialize(result));
        Assert.Equal("channel_invalid", (await transport.SendAsync(lease with { OrganizationId = Guid.NewGuid() }, default)).FailureCode);
        foreach (var url in new[] { "http://jira.invalid", "https://jira.invalid?token=secret", "https://user:pass@jira.invalid", "https://jira.invalid/path" })
        {
            using var capture = new JiraHandler(201);
            using var invalid = new CentralWebhookTransport(Configuration(lease, url), capture);
            Assert.Equal("channel_invalid", (await invalid.SendAsync(lease, default)).FailureCode);
            Assert.Null(capture.Uri);
        }
    }

    private static IConfiguration Configuration(NotificationDeliveryLease lease, string url = "https://jira.invalid")
    {
        var prefix = $"CentralNotifications:Organizations:{lease.TenantId:D}:{lease.OrganizationId:D}:Channels:jira";
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{prefix}:Kind"] = "Jira", [$"{prefix}:Url"] = url, [$"{prefix}:UserName"] = "bot@example.invalid",
            [$"{prefix}:ApiToken"] = "test-token-secret", [$"{prefix}:ProjectKey"] = "SEC", [$"{prefix}:IssueType"] = "Task"
        }).Build();
    }

    private static NotificationDeliveryLease Lease()
    {
        var now = DateTime.UtcNow;
        var value = new SharedDriftEventDto(Guid.NewGuid(), Guid.Empty, Guid.Empty, CloudProvider.Aws,
            "123456789012", "scan", "Changed", Guid.NewGuid().ToString(), SharedAlertSeverity.High, null, null, now)
            { EventType = "scan.failed", SubjectId = Guid.NewGuid() };
        return new(Guid.NewGuid(), Guid.NewGuid(),
            new(Guid.NewGuid(), value.EventId, Guid.Empty, "jira", "Sending", 1, now, null, null, null) { ConnectorKind = "Jira" },
            Guid.NewGuid(), value);
    }

    private sealed class JiraHandler(int status) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string Body { get; private set; } = "";
        public bool Fail { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail) throw new HttpRequestException("test-token-secret sensitive response");
            Uri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new((HttpStatusCode)status) { Content = new UnreadableContent() };
        }
    }

    private sealed class UnreadableContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new InvalidOperationException("response-secret must not be read");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
