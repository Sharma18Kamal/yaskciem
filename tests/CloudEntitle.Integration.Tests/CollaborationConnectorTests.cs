using System.Net;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class CollaborationConnectorTests
{
    [Theory]
    [InlineData(SecurityConnectorCatalog.ServiceNow, 201, true, false)]
    [InlineData(SecurityConnectorCatalog.ServiceNow, 200, false, false)]
    [InlineData(SecurityConnectorCatalog.ServiceNow, 429, false, true)]
    [InlineData(SecurityConnectorCatalog.ServiceNow, 503, false, false)]
    [InlineData(SecurityConnectorCatalog.Teams, 202, true, false)]
    [InlineData(SecurityConnectorCatalog.Teams, 302, false, false)]
    [InlineData(SecurityConnectorCatalog.Teams, 503, false, false)]
    public async Task Collaboration_UsesNativePayloadAndDoesNotRetryUncertainSends(string kind, int code, bool delivered, bool retry)
    {
        var lease = Lease(kind);
        var root = $"CentralNotifications:Organizations:{lease.TenantId:D}:{lease.OrganizationId:D}:Channels:client";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{root}:Kind"] = kind, [$"{root}:Url"] = kind == SecurityConnectorCatalog.ServiceNow
                ? "https://client.service-now.com" : "https://client.environment.api.powerplatform.com/powerautomate/automations/direct/workflows/client/triggers/manual/paths/invoke?sig=fixture/",
            [$"{root}:UserName"] = "bot", [$"{root}:ApiToken"] = "test-secret"
        }).Build();
        using var handler = new Handler(code);
        using var transport = new CentralWebhookTransport(configuration, handler);
        var result = await transport.SendAsync(lease, default);
        Assert.Equal(delivered, result.Delivered);
        Assert.Equal(retry, result.Retryable);
        using var payload = JsonDocument.Parse(handler.Body!);
        if (kind == SecurityConnectorCatalog.ServiceNow)
        {
            Assert.Equal(lease.Delivery.DeliveryId.ToString("D"), payload.RootElement.GetProperty("correlation_id").GetString());
            Assert.Equal("/api/now/table/incident", handler.Uri!.AbsolutePath);
            Assert.Equal("Basic", handler.Authentication);
        }
        else
        {
            Assert.Equal("message", payload.RootElement.GetProperty("type").GetString());
            Assert.Equal("?sig=fixture/", handler.Uri!.Query);
            Assert.Null(handler.Authentication);
        }
        Assert.DoesNotContain("test-secret", handler.Body!);
        configuration[$"{root}:Url"] = "https://evil.invalid/path";
        using var invalid = new Handler(200);
        using var rejected = new CentralWebhookTransport(configuration, invalid);
        Assert.Equal("channel_invalid", (await rejected.SendAsync(lease, default)).FailureCode);
        Assert.Null(invalid.Uri);
    }
    private static NotificationDeliveryLease Lease(string kind)
    {
        var now = DateTime.UtcNow;
        var evt = new SharedDriftEventDto(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, CloudProvider.Azure, "account", "behavior", "Added",
            "signal", SharedAlertSeverity.Medium, null, null, now) { EventType = "behavior.anomaly" };
        return new(Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), evt.EventId, Guid.NewGuid(), "client", "Sending", 1, now, null, null, null)
            { ConnectorKind = kind }, Guid.NewGuid(), evt);
    }
    private sealed class Handler(int code) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Authentication { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Authentication = request.Headers.Authorization?.Scheme;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new((HttpStatusCode)code) { Content = new StringContent("{}") };
        }
    }
}
