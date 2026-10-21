using System.Globalization;
using System.Text;
using System.Text.Json;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.LicenseServer.Services;

public sealed record NotificationSendResult(bool Delivered, bool Retryable, int? HttpStatus, string? FailureCode, bool Suppressed = false)
{
    public static bool IsSafe(NotificationSendResult result)
        => result.HttpStatus is null or >= 100 and <= 599
            && result.FailureCode is null or "channel_unconfigured" or "channel_invalid" or "network_error"
                or "timeout" or "http_rejected" or "rule_disabled" or "worker_shutdown"
            && !(result.Delivered && result.Suppressed);
}

public interface ICentralNotificationTransport
{
    Task<NotificationSendResult> SendAsync(NotificationDeliveryLease lease, CancellationToken cancellationToken);
}

public sealed class CentralWebhookTransport : ICentralNotificationTransport, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly HttpClient _client;

    public CentralWebhookTransport(IConfiguration configuration)
        : this(configuration, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = null }) { }

    public CentralWebhookTransport(IConfiguration configuration, HttpMessageHandler handler)
    {
        _configuration = configuration;
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<NotificationSendResult> SendAsync(NotificationDeliveryLease lease, CancellationToken cancellationToken)
    {
        var section = _configuration.GetSection($"CentralNotifications:Organizations:{lease.TenantId:D}:{lease.OrganizationId:D}:Channels:{lease.Delivery.Channel}");
        var endpoint = section["Url"];
        var key = section["SigningKey"];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
            return new(false, true, null, "channel_unconfigured");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || Encoding.UTF8.GetByteCount(key) < 32)
            return new(false, false, null, "channel_invalid");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = "v1", lease.TenantId, lease.OrganizationId, lease.Delivery.DeliveryId, Event = lease.Event
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = NotificationWebhookSignature.Sign(body, lease.Event.EventId, timestamp, key);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Yask-Event-Id", lease.Event.EventId.ToString("D"));
        request.Headers.Add("X-Yask-Delivery-Id", lease.Delivery.DeliveryId.ToString("D"));
        request.Headers.Add("X-Yask-Timestamp", timestamp);
        request.Headers.Add("X-Yask-Signature", signature);
        request.Headers.Add("Idempotency-Key", lease.Delivery.DeliveryId.ToString("D"));
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            return response.IsSuccessStatusCode
                ? new(true, false, status, null)
                : new(false, status is 408 or 429 or >= 500, status, "http_rejected");
        }
        catch (HttpRequestException)
        {
            return new(false, true, null, "network_error");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, true, null, "timeout");
        }
    }

    public void Dispose() => _client.Dispose();
}

public sealed class CentralNotificationWorker(SharedWorkspaceStore store, ICentralNotificationTransport transport,
    ILogger<CentralNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var lease = await store.ClaimNotificationDeliveryAsync(stoppingToken);
                if (lease is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }
                NotificationSendResult result;
                try
                {
                    result = await store.IsNotificationEnabledAsync(lease, stoppingToken)
                        ? await transport.SendAsync(lease, stoppingToken)
                        : new(false, false, null, "rule_disabled", Suppressed: true);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    result = new(false, true, null, "worker_shutdown");
                }
                using var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await store.CompleteNotificationDeliveryAsync(lease, result, completionTimeout.Token);
                if (!result.Delivered)
                    logger.LogWarning("Central notification delivery {DeliveryId} returned {FailureCode}; HTTP status {Status}.",
                        lease.Delivery.DeliveryId, result.FailureCode, result.HttpStatus);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("Central notification queue failed ({ErrorType}); delivery leases will recover automatically.", exception.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
