using System.Text;
using System.Text.Json;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.LicenseServer.Services;

public sealed partial class CentralWebhookTransport
{
    private async Task<NotificationSendResult> SendCollaborationAsync(NotificationDeliveryLease lease,
        IConfigurationSection section, CancellationToken token)
    {
        var kind = lease.Delivery.ConnectorKind;
        if (ConnectorConfiguration.Status(section, kind) != "Configured")
            return new(false, false, null, "channel_invalid");
        var details = $"Event: {lease.Event.EventType}\nEvent ID: {lease.Event.EventId:D}\nDelivery ID: {lease.Delivery.DeliveryId:D}\n"
            + $"Provider: {lease.Event.Provider}\nAccount: {lease.Event.ExternalAccountId}\nDetected UTC: {lease.Event.DetectedAtUtc:O}\n"
            + "Governance notification only. This does not approve or change cloud access.";
        object body = kind == SecurityConnectorCatalog.ServiceNow
            ? new { short_description = $"YASK-CIEM {lease.Event.EventType} ({lease.Event.Provider})", description = details,
                correlation_id = lease.Delivery.DeliveryId.ToString("D") }
            : new { type = "message", attachments = new[] { new { contentType = "application/vnd.microsoft.card.adaptive",
                contentUrl = (string?)null, content = new Dictionary<string, object>
                {
                    ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json", ["type"] = "AdaptiveCard", ["version"] = "1.4",
                    ["body"] = new object[] { new { type = "TextBlock", text = "YASK-CIEM security notification", weight = "Bolder", wrap = true },
                        new { type = "TextBlock", text = details, wrap = true } }
                } } } };
        var uri = new Uri(kind == SecurityConnectorCatalog.ServiceNow ? section["Url"]!.TrimEnd('/') + "/api/now/table/incident" : section["Url"]!);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body)) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("Idempotency-Key", lease.Delivery.DeliveryId.ToString("D"));
        if (kind == SecurityConnectorCatalog.ServiceNow)
            request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{section["UserName"]}:{section["ApiToken"]}")));
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var status = (int)response.StatusCode;
            var accepted = kind == SecurityConnectorCatalog.ServiceNow ? status == 201 : response.IsSuccessStatusCode;
            return accepted ? new(true, false, status, null)
                : status == 429 ? new(false, true, status, "http_rejected")
                : new(false, false, status, status >= 500 || status == 408 ? "outcome_unknown" : "http_rejected");
        }
        catch (HttpRequestException) { return new(false, false, null, "outcome_unknown"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(false, false, null, "outcome_unknown"); }
    }
}
