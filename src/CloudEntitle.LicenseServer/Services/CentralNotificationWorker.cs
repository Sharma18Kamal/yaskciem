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
                or "timeout" or "http_rejected" or "rule_disabled" or "worker_shutdown" or "outcome_unknown"
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
        if (ConnectorConfiguration.Kind(section) != lease.Delivery.ConnectorKind)
            return new(false, false, null, "channel_invalid");
        if (lease.Delivery.ConnectorKind == "Jira")
            return await SendJiraAsync(lease, section, cancellationToken);
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
        request.Headers.Add("X-Yask-Event-Type", lease.Event.EventType);
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

    private async Task<NotificationSendResult> SendJiraAsync(NotificationDeliveryLease lease, IConfigurationSection section, CancellationToken token)
    {
        if (ConnectorConfiguration.Status(section, "Jira") != "Configured")
            return new(false, false, null, "channel_invalid");
        var uri = new Uri(section["Url"]!.TrimEnd('/') + "/rest/api/3/issue");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            fields = new
            {
                project = new { key = section["ProjectKey"] },
                issuetype = new { name = section["IssueType"] },
                summary = $"YASK-CIEM {lease.Event.EventType} ({lease.Event.Provider})",
                labels = new[] { "yask-ciem", $"yask-delivery-{lease.Delivery.DeliveryId:D}" },
                description = new
                {
                    type = "doc", version = 1,
                    content = new[] { new { type = "paragraph", content = new[] { new { type = "text",
                        text = $"YASK-CIEM operational event\nEvent: {lease.Event.EventType}\nEvent ID: {lease.Event.EventId:D}\nDelivery ID: {lease.Delivery.DeliveryId:D}\nProvider: {lease.Event.Provider}\nAccount: {lease.Event.ExternalAccountId}\nEntity: {lease.Event.EntityKey}\nSubject: {lease.Event.SubjectId}\nDetected UTC: {lease.Event.DetectedAtUtc:O}\nThis ticket is a governance record. It does not change cloud access." } } } }
                }
            }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{section["UserName"]}:{section["ApiToken"]}")));
        request.Headers.Add("Idempotency-Key", lease.Delivery.DeliveryId.ToString("D"));
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var status = (int)response.StatusCode;
            return status == 201 ? new(true, false, status, null)
                : status == 429 ? new(false, true, status, "http_rejected")
                : new(false, false, status, status >= 500 || status == 408 ? "outcome_unknown" : "http_rejected");
        }
        catch (HttpRequestException) { return new(false, false, null, "outcome_unknown"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(false, false, null, "outcome_unknown"); }
    }

    public void Dispose() => _client.Dispose();
}

public static class ConnectorConfiguration
{
    public static string Kind(IConfiguration section) => section["Kind"] ?? "SignedWebhook";

    public static string Status(IConfiguration section, string kind)
    {
        if (section["Url"] is null) return "Unconfigured";
        if (Kind(section) != kind || kind is not ("SignedWebhook" or "Jira")
            || !Uri.TryCreate(section["Url"], UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return "Invalid";
        return kind == "SignedWebhook"
            ? Encoding.UTF8.GetByteCount(section["SigningKey"] ?? "") >= 32 ? "Configured" : "Invalid"
            : string.IsNullOrEmpty(uri.Query) && uri.AbsolutePath == "/"
                && !string.IsNullOrWhiteSpace(section["UserName"]) && !section["UserName"]!.Contains(':')
                && !string.IsNullOrWhiteSpace(section["ApiToken"])
                && section["ProjectKey"] is { Length: > 0 and <= 64 } project && project.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
                && section["IssueType"] is { Length: > 0 and <= 100 } ? "Configured" : "Invalid";
    }
}

public sealed class CentralNotificationWorker : BackgroundService
{
    private readonly SharedWorkspaceStore _store;
    private readonly ICentralNotificationTransport _transport;
    private readonly ILogger<CentralNotificationWorker> _logger;
    private readonly WorkerHealthRegistry _workers;
    private readonly OperationsMetrics _metrics;

    public CentralNotificationWorker(SharedWorkspaceStore store, ICentralNotificationTransport transport,
        ILogger<CentralNotificationWorker> logger, WorkerHealthRegistry? workers = null, OperationsMetrics? metrics = null)
    {
        _store = store;
        _transport = transport;
        _logger = logger;
        _workers = workers ?? new WorkerHealthRegistry();
        _metrics = metrics ?? new OperationsMetrics();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var lease = await _store.ClaimNotificationDeliveryAsync(stoppingToken);
                _workers.Heartbeat("notifications", success: true);
                if (lease is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }
                NotificationSendResult result;
                try
                {
                    result = await _store.IsNotificationEnabledAsync(lease, stoppingToken)
                        ? await _transport.SendAsync(lease, stoppingToken)
                        : new(false, false, null, "rule_disabled", Suppressed: true);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    result = new(false, true, null, "worker_shutdown");
                }
                using var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _store.CompleteNotificationDeliveryAsync(lease, result, completionTimeout.Token);
                if (!result.Delivered)
                {
                    _metrics.RecordConnectorFailure(lease.Delivery.ConnectorKind, result.FailureCode);
                    _logger.LogWarning("Central notification delivery {DeliveryId} returned {FailureCode}; HTTP status {Status}.",
                        lease.Delivery.DeliveryId, result.FailureCode, result.HttpStatus);
                }
                _workers.Heartbeat("notifications", success: true);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                _workers.Heartbeat("notifications", failed: true);
                _logger.LogError("Central notification queue failed ({ErrorType}); delivery leases will recover automatically.", exception.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
