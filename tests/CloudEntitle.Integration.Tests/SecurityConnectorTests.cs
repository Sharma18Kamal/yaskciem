using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class SecurityConnectorTests
{
    [Theory]
    [InlineData(200, "{\"code\":0}", true, false)]
    [InlineData(200, "{\"code\":6,\"text\":\"Invalid event\"}", false, false)]
    [InlineData(200, "{\"code\":\"0\"}", false, false)]
    [InlineData(200, "[]", false, false)]
    [InlineData(200, "not-json", false, false)]
    [InlineData(401, "private error", false, false)]
    [InlineData(302, "", false, false)]
    [InlineData(429, "", false, true)]
    [InlineData(503, "", false, true)]
    public async Task Splunk_UsesHecEnvelopeAndRequiresReceiverCodeZero(
        int status, string response, bool delivered, bool retryable)
    {
        var lease = Lease(SecurityConnectorCatalog.Splunk);
        using var handler = new CaptureHandler(status, response);
        using var transport = new CentralWebhookTransport(Configuration(lease), handler);

        var result = await transport.SendAsync(lease, default);

        Assert.Equal(delivered, result.Delivered);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal("Splunk", handler.AuthenticationScheme);
        Assert.Equal("server-only-token", handler.AuthenticationToken);
        Assert.Equal("https://splunk.invalid:8088/services/collector/event", handler.Uri!.AbsoluteUri);
        Assert.Equal(lease.Delivery.DeliveryId.ToString("D"), handler.IdempotencyKey);
        using var document = JsonDocument.Parse(handler.Body);
        Assert.Equal("security", document.RootElement.GetProperty("index").GetString());
        Assert.Equal("yask:ciem", document.RootElement.GetProperty("sourcetype").GetString());
        var value = document.RootElement.GetProperty("event");
        Assert.Equal("finding.created", value.GetProperty("eventType").GetString());
        Assert.Equal("Aws", value.GetProperty("provider").GetString());
        Assert.Equal(lease.OrganizationId.ToString("D"), value.GetProperty("organizationId").GetString());
        Assert.DoesNotContain("server-only-token", handler.Body);
        Assert.DoesNotContain("private error", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(204, true, false)]
    [InlineData(200, false, false)]
    [InlineData(400, false, false)]
    [InlineData(401, false, false)]
    [InlineData(302, false, false)]
    [InlineData(429, false, true)]
    [InlineData(503, false, true)]
    public async Task Sentinel_UsesDcrStreamAndMonitorScopedManagedIdentity(int status, bool delivered, bool retryable)
    {
        var lease = Lease(SecurityConnectorCatalog.Sentinel);
        var credential = new CaptureCredential();
        using var handler = new CaptureHandler(status, "");
        using var transport = new CentralWebhookTransport(Configuration(lease), handler, credential);

        var result = await transport.SendAsync(lease, default);

        Assert.Equal(delivered, result.Delivered);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal("https://monitor.azure.com/.default", Assert.Single(credential.Scopes));
        Assert.Equal("Bearer", handler.AuthenticationScheme);
        Assert.Equal("server-only-access-token", handler.AuthenticationToken);
        Assert.Equal("https://client.ingest.monitor.azure.com/dataCollectionRules/dcr-123/streams/Custom-YaskCiem?api-version=2023-01-01",
            handler.Uri!.AbsoluteUri);
        using var document = JsonDocument.Parse(handler.Body);
        var row = Assert.Single(document.RootElement.EnumerateArray());
        Assert.True(row.TryGetProperty("TimeGenerated", out _));
        Assert.Equal(lease.Event.EventId.ToString("D"), row.GetProperty("Event").GetProperty("eventId").GetString());
        Assert.DoesNotContain("server-only-access-token", handler.Body);
    }

    [Theory]
    [InlineData(SecurityConnectorCatalog.Splunk, "http://splunk.invalid/services/collector/event")]
    [InlineData(SecurityConnectorCatalog.Splunk, "https://splunk.invalid/services/collector/event?token=secret")]
    [InlineData(SecurityConnectorCatalog.Sentinel, "https://untrusted.invalid")]
    [InlineData(SecurityConnectorCatalog.Sentinel, "https://client.ingest.monitor.azure.com.bad.invalid")]
    [InlineData(SecurityConnectorCatalog.Sentinel, "https://client.ingest.monitor.azure.com/path")]
    [InlineData(SecurityConnectorCatalog.QRadar, "tcp://qradar.invalid:514")]
    [InlineData(SecurityConnectorCatalog.QRadar, "tls://qradar.invalid:6514?token=secret")]
    public async Task UnsafeEndpoints_AreRejectedBeforeSending(string kind, string url)
    {
        var lease = Lease(kind);
        var configuration = Configuration(lease);
        configuration[$"{Prefix(lease)}:Url"] = url;
        using var handler = new CaptureHandler(200, "{\"code\":0}");
        var syslog = new CaptureSyslogSender();
        using var transport = new CentralWebhookTransport(configuration, handler, new CaptureCredential(), syslog);

        Assert.Equal("channel_invalid", (await transport.SendAsync(lease, default)).FailureCode);
        Assert.Null(handler.Uri);
        Assert.Null(syslog.Message);
    }

    [Fact]
    public async Task Qradar_ProducesSingleLineLeefWithScopedIdentifiersAndNoSecrets()
    {
        var lease = Lease(SecurityConnectorCatalog.QRadar);
        lease = lease with { Event = lease.Event with { EntityKey = "entity\ninjected\ttab|pipe" } };
        var sender = new CaptureSyslogSender();
        using var handler = new CaptureHandler(200, "");
        using var transport = new CentralWebhookTransport(Configuration(lease), handler, syslogSender: sender);

        Assert.True((await transport.SendAsync(lease, default)).Delivered);
        var text = Encoding.UTF8.GetString(sender.Message!);
        Assert.StartsWith("<134>1 ", text);
        Assert.Contains(" yask-client YASK-CIEM - - - LEEF:2.0|YASK|CIEM|1.0|finding.created|0x09|", text);
        Assert.Contains($"organizationId={lease.OrganizationId:D}", text);
        Assert.Contains($"deliveryId={lease.Delivery.DeliveryId:D}", text);
        Assert.Contains("entityKey=entity\\ninjected\\ttab\\|pipe", text);
        Assert.Equal(1, text.Count(c => c == '\n'));
        Assert.EndsWith("\n", text);
        Assert.DoesNotContain("server-only", text);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task Splunk_RejectsOversizedAcknowledgementsAndSanitizesNetworkErrors()
    {
        var lease = Lease(SecurityConnectorCatalog.Splunk);
        using var huge = new CaptureHandler(200, new string('x', 4097));
        using var transport = new CentralWebhookTransport(Configuration(lease), huge);
        Assert.Equal("outcome_unknown", (await transport.SendAsync(lease, default)).FailureCode);
        using var failure = new CaptureHandler(200, "") { FailNetwork = true };
        using var failedTransport = new CentralWebhookTransport(Configuration(lease), failure);
        var result = await failedTransport.SendAsync(lease, default);
        Assert.Equal("network_error", result.FailureCode);
        Assert.DoesNotContain("server-only", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Sentinel_ReportsUnavailableCredentialsAndDoesNotSend()
    {
        var lease = Lease(SecurityConnectorCatalog.Sentinel);
        using var handler = new CaptureHandler(204, "");
        using var transport = new CentralWebhookTransport(Configuration(lease), handler,
            new CaptureCredential { Unavailable = true });
        var result = await transport.SendAsync(lease, default);
        Assert.Equal("channel_unconfigured", result.FailureCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public void RulesAndCatalog_ExposeOnlyImplementedOutboundCapabilities()
    {
        foreach (var kind in new[] { SecurityConnectorCatalog.Splunk, SecurityConnectorCatalog.QRadar, SecurityConnectorCatalog.Sentinel })
        {
            SharedWorkspaceStore.ValidateNotificationRule(new(Guid.NewGuid(), kind, true, SharedAlertSeverity.High,
                true, true, true, true, true, "client-security") { ConnectorKind = kind });
            Assert.True(Assert.Single(SecurityConnectorCatalog.Capabilities, item => item.Kind == kind).ExportEvents);
        }
        Assert.All(SecurityConnectorCatalog.Capabilities, item => Assert.False(item.ExecuteResponseActions));
        Assert.All(SecurityConnectorCatalog.Capabilities, item => Assert.False(item.ImportAlerts));
        Assert.Throws<ArgumentException>(() => SharedWorkspaceStore.ValidateNotificationRule(
            new(Guid.NewGuid(), "Not supported", true, SharedAlertSeverity.High, true, true, true, true, true, "client-security")
            { ConnectorKind = "CarbonBlackCloud" }));
    }

    private static string Prefix(NotificationDeliveryLease lease)
        => $"CentralNotifications:Organizations:{lease.TenantId:D}:{lease.OrganizationId:D}:Channels:security";

    private static IConfiguration Configuration(NotificationDeliveryLease lease)
    {
        var prefix = Prefix(lease);
        var url = lease.Delivery.ConnectorKind switch
        {
            SecurityConnectorCatalog.Splunk => "https://splunk.invalid:8088/services/collector/event",
            SecurityConnectorCatalog.Sentinel => "https://client.ingest.monitor.azure.com",
            _ => "tls://qradar.invalid:6514"
        };
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{prefix}:Kind"] = lease.Delivery.ConnectorKind,
            [$"{prefix}:Url"] = url,
            [$"{prefix}:Token"] = "server-only-token",
            [$"{prefix}:Index"] = "security",
            [$"{prefix}:RuleId"] = "dcr-123",
            [$"{prefix}:Stream"] = "Custom-YaskCiem",
            [$"{prefix}:LogSourceIdentifier"] = "yask-client",
            [$"{prefix}:ClientCertificatePath"] = Path.GetFullPath("client.pfx"),
            [$"{prefix}:ClientCertificatePassword"] = "server-only-password"
        }).Build();
    }

    private static NotificationDeliveryLease Lease(string kind)
    {
        var now = DateTime.UtcNow;
        var value = new SharedDriftEventDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CloudProvider.Aws,
            "123456789012", "finding", "Added", "entity", SharedAlertSeverity.High, null, "hash", now);
        return new(Guid.NewGuid(), Guid.NewGuid(),
            new(Guid.NewGuid(), value.EventId, Guid.NewGuid(), "security", "Sending", 1, now, null, null, null)
            { ConnectorKind = kind }, Guid.NewGuid(), value);
    }

    private sealed class CaptureHandler(int status, string response) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? AuthenticationScheme { get; private set; }
        public string? AuthenticationToken { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string Body { get; private set; } = "";
        public bool FailNetwork { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (FailNetwork) throw new HttpRequestException("server-only-token must not leak");
            Uri = request.RequestUri;
            AuthenticationScheme = request.Headers.Authorization?.Scheme;
            AuthenticationToken = request.Headers.Authorization?.Parameter;
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            Body = await request.Content!.ReadAsStringAsync(token);
            return new((HttpStatusCode)status) { Content = new StringContent(response) };
        }
    }

    private sealed class CaptureCredential : TokenCredential
    {
        public string[] Scopes { get; private set; } = [];
        public bool Unavailable { get; init; }
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token)
        {
            Scopes = context.Scopes;
            if (Unavailable) throw new CredentialUnavailableException("server-only credential detail");
            return new("server-only-access-token", DateTimeOffset.UtcNow.AddMinutes(30));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token)
            => ValueTask.FromResult(GetToken(context, token));
    }

    private sealed class CaptureSyslogSender : IQradarSyslogSender
    {
        public byte[]? Message { get; private set; }
        public Task<NotificationSendResult> SendAsync(IConfiguration configuration, byte[] message, CancellationToken token)
        {
            Message = message;
            return Task.FromResult(new NotificationSendResult(true, false, null, null));
        }
    }
}
