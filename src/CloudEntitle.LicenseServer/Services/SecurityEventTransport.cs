using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.LicenseServer.Services;

public sealed partial class CentralWebhookTransport
{
    private readonly TokenCredential? _sentinelCredential;
    private readonly IQradarSyslogSender _syslogSender;
    private readonly ConcurrentDictionary<string, TokenCredential> _managedIdentities = new(StringComparer.Ordinal);

    private async Task<NotificationSendResult> SendSecurityEventAsync(
        NotificationDeliveryLease lease, IConfiguration section, CancellationToken token)
    {
        var kind = lease.Delivery.ConnectorKind;
        var configurationStatus = ConnectorConfiguration.Status(section, kind);
        if (configurationStatus != "Configured")
            return new(false, configurationStatus == "Unconfigured", null,
                configurationStatus == "Unconfigured" ? "channel_unconfigured" : "channel_invalid");

        var isSplunk = kind == SecurityConnectorCatalog.Splunk;
        var endpoint = isSplunk ? section["Url"]!
            : $"{section["Url"]!.TrimEnd('/')}/dataCollectionRules/{section["RuleId"]}/streams/{section["Stream"]}?api-version=2023-01-01";
        var eventBody = new
        {
            version = "v1",
            tenantId = lease.TenantId,
            organizationId = lease.OrganizationId,
            deliveryId = lease.Delivery.DeliveryId,
            eventId = lease.Event.EventId,
            eventType = lease.Event.EventType,
            provider = lease.Event.Provider.ToString(),
            accountId = lease.Event.ExternalAccountId,
            severity = lease.Event.Severity.ToString(),
            entityKey = lease.Event.EntityKey,
            subjectId = lease.Event.SubjectId,
            scanId = lease.Event.ScanId,
            previousScanId = lease.Event.PreviousScanId,
            previousStateHash = lease.Event.PreviousStateHash,
            currentStateHash = lease.Event.CurrentStateHash,
            detectedAtUtc = lease.Event.DetectedAtUtc.ToUniversalTime()
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var body = isSplunk
            ? JsonSerializer.SerializeToUtf8Bytes(new
            {
                time = new DateTimeOffset(lease.Event.DetectedAtUtc.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000d,
                source = "YASK-CIEM",
                sourcetype = "yask:ciem",
                index = section["Index"],
                @event = eventBody
            }, options)
            : JsonSerializer.SerializeToUtf8Bytes(new[]
            {
                new { TimeGenerated = lease.Event.DetectedAtUtc.ToUniversalTime(), Event = eventBody }
            });

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("Idempotency-Key", lease.Delivery.DeliveryId.ToString("D"));
        request.Headers.Add("X-Yask-Event-Id", lease.Event.EventId.ToString("D"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            if (isSplunk)
                request.Headers.Authorization = new AuthenticationHeaderValue("Splunk", section["Token"]);
            else
            {
                var credential = _sentinelCredential ?? _managedIdentities.GetOrAdd(
                    section["ManagedIdentityClientId"] ?? "",
                    clientId => new ManagedIdentityCredential(string.IsNullOrEmpty(clientId)
                        ? ManagedIdentityId.SystemAssigned : ManagedIdentityId.FromUserAssignedClientId(clientId)));
                var accessToken = await credential.GetTokenAsync(
                    new TokenRequestContext(["https://monitor.azure.com/.default"]), timeout.Token);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
            }

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (isSplunk && response.IsSuccessStatusCode)
            {
                // HEC can reject an event in a JSON response even when HTTP succeeds.
                var accepted = await HasSuccessfulHecCodeAsync(response.Content, timeout.Token);
                return new(accepted, false, status, accepted ? null : "outcome_unknown");
            }
            return !isSplunk && status == 204
                ? new(true, false, status, null)
                : new(false, status is 408 or 429 or >= 500, status, "http_rejected");
        }
        catch (CredentialUnavailableException) { return new(false, true, null, "channel_unconfigured"); }
        catch (AuthenticationFailedException) { return new(false, false, null, "channel_invalid"); }
        catch (HttpRequestException) { return new(false, true, null, "network_error"); }
        catch (IOException) { return new(false, true, null, "network_error"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(false, true, null, "timeout"); }
    }

    private static async Task<bool> HasSuccessfulHecCodeAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > 4096) return false;
        await using var stream = await content.ReadAsStreamAsync(token);
        var buffer = new byte[4097];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0) break;
            length += read;
        }
        if (length == 0 || length > 4096) return false;
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) && value == 0;
        }
        catch (JsonException) { return false; }
    }
}
