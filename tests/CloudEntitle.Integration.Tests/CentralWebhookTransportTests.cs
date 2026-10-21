using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class CentralWebhookTransportTests
{
    [Theory]
    [InlineData(204, true, false)]
    [InlineData(400, false, false)]
    [InlineData(302, false, false)]
    [InlineData(429, false, true)]
    [InlineData(503, false, true)]
    public async Task Transport_ShouldSignStableIdsAndNeverReadResponseBodies(int status, bool delivered, bool retry)
    {
        var lease = Lease();
        const string key = "test-only-signing-key-with-at-least-32-bytes";
        using var handler = new CapturingHandler(status);
        using var transport = new CentralWebhookTransport(Configuration(lease, "https://receiver.invalid/hook?token=do-not-log", key), handler);
        var result = await transport.SendAsync(lease, default);
        Assert.Equal(delivered, result.Delivered);
        Assert.Equal(retry, result.Retryable);
        Assert.Equal(status, result.HttpStatus);
        Assert.DoesNotContain("do-not-log", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("response-secret", JsonSerializer.Serialize(result));
        Assert.Equal(lease.Event.EventId.ToString("D"), handler.Headers["X-Yask-Event-Id"]);
        Assert.Equal(lease.Delivery.DeliveryId.ToString("D"), handler.Headers["Idempotency-Key"]);
        var timestamp = handler.Headers["X-Yask-Timestamp"];
        Assert.True(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(timestamp, CultureInfo.InvariantCulture)) < 10);
        var signed = Encoding.UTF8.GetBytes($"{timestamp}.{lease.Event.EventId:D}.{handler.Body}");
        var expected = $"v1={Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), signed)).ToLowerInvariant()}";
        Assert.Equal(expected, handler.Headers["X-Yask-Signature"]);
        Assert.True(NotificationWebhookSignature.Verify(Encoding.UTF8.GetBytes(handler.Body), lease.Event.EventId, timestamp, expected, key, DateTimeOffset.UtcNow));
        Assert.False(NotificationWebhookSignature.Verify(Encoding.UTF8.GetBytes(handler.Body + " "), lease.Event.EventId, timestamp, expected, key, DateTimeOffset.UtcNow));
        Assert.False(NotificationWebhookSignature.Verify(Encoding.UTF8.GetBytes(handler.Body), Guid.NewGuid(), timestamp, expected, key, DateTimeOffset.UtcNow));
        Assert.False(NotificationWebhookSignature.Verify(Encoding.UTF8.GetBytes(handler.Body), lease.Event.EventId, timestamp, expected, key, DateTimeOffset.UtcNow.AddMinutes(6)));
        Assert.False(NotificationWebhookSignature.Verify(Encoding.UTF8.GetBytes(handler.Body), lease.Event.EventId, timestamp, expected, key, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var originalBody = handler.Body;
        await transport.SendAsync(lease with { AttemptId = Guid.NewGuid() }, default);
        Assert.Equal(originalBody, handler.Body);
        Assert.Equal(lease.Delivery.DeliveryId.ToString("D"), handler.Headers["Idempotency-Key"]);
    }

    [Theory]
    [InlineData(null, null, "channel_unconfigured")]
    [InlineData("http://receiver.invalid/hook", "a-long-enough-test-key-with-32-characters", "channel_invalid")]
    [InlineData("https://user:password@receiver.invalid", "a-long-enough-test-key-with-32-characters", "channel_invalid")]
    [InlineData("https://receiver.invalid", "short", "channel_invalid")]
    public async Task Transport_ShouldRejectMissingOrUnsafeConfigurationWithoutSending(string? url, string? key, string failure)
    {
        var lease = Lease();
        using var handler = new CapturingHandler(204);
        using var transport = new CentralWebhookTransport(Configuration(lease, url, key), handler);
        Assert.Equal(failure, (await transport.SendAsync(lease, default)).FailureCode);
        Assert.Empty(handler.Headers);
    }

    [Fact]
    public async Task Transport_ShouldUseOrganizationSpecificChannelAndSanitizeNetworkErrors()
    {
        var lease = Lease();
        using var handler = new FailingHandler();
        using var transport = new CentralWebhookTransport(Configuration(lease, "https://receiver.invalid?secret=private",
            "a-long-enough-test-key-with-32-characters"), handler);
        var result = await transport.SendAsync(lease, default);
        Assert.Equal("network_error", result.FailureCode);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result));
        Assert.Equal("channel_unconfigured", (await transport.SendAsync(lease with { OrganizationId = Guid.NewGuid() }, default)).FailureCode);
    }

    private static IConfiguration Configuration(NotificationDeliveryLease lease, string? url, string? key)
    {
        var prefix = $"CentralNotifications:Organizations:{lease.TenantId:D}:{lease.OrganizationId:D}:Channels:security";
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{prefix}:Url"] = url, [$"{prefix}:SigningKey"] = key
        }).Build();
    }

    private static NotificationDeliveryLease Lease()
    {
        var now = DateTime.UtcNow;
        var drift = new SharedDriftEventDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CloudProvider.Aws,
            "123456789012", "effective-access", "Added", "stable-key-hash", SharedAlertSeverity.High, null, "state-hash", now);
        var delivery = new SharedDeliveryDto(Guid.NewGuid(), drift.EventId, Guid.NewGuid(), "security", "Sending", 1, now, null, null, null);
        return new(Guid.NewGuid(), Guid.NewGuid(), delivery, Guid.NewGuid(), drift);
    }

    private sealed class CapturingHandler(int status) : HttpMessageHandler
    {
        public Dictionary<string, string> Headers { get; } = new();
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            foreach (var header in request.Headers) Headers[header.Key] = header.Value.Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new UnreadableContent() };
        }
    }

    private sealed class UnreadableContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new InvalidOperationException("response-secret must never be read");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Failed https://receiver.invalid?secret=private response-secret");
    }
}
