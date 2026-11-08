namespace CloudEntitle.Integration.Contracts;

/// <summary>A normalized, untrusted SIEM alert revision supplied by an authorized client bridge.</summary>
public sealed record InboundSecurityAlert(string AlertId, DateTimeOffset UpdatedAtUtc, string Title,
    string Severity, string Status, string? Principal, string? Resource, string? EvidenceUrl);

/// <summary>An idempotent connector batch with an opaque compare-and-swap cursor; never a cloud response instruction.</summary>
public sealed record ImportSecurityAlertsRequest(Guid BatchId, string? PreviousCursor, string Cursor,
    IReadOnlyList<InboundSecurityAlert> Alerts);

/// <summary>Committed receipt; Revisions counts accepted entries, including exact existing revisions. Never remediation approval.</summary>
public sealed record SecurityAlertImportReceipt(Guid BatchId, string Product, string Channel, string Cursor, int Revisions);

/// <summary>Latest imported alert revision and receiving actor for the selected connector.</summary>
public sealed record StoredSecurityAlert(string Product, string Channel, Guid UploadedBy,
    DateTimeOffset ReceivedAtUtc, InboundSecurityAlert Alert);

/// <summary>Server-known cursor; null means no successful import for this connector scope.</summary>
public sealed record SecurityAlertCursor(string Product, string Channel, string? Cursor);

public static class InboundSecurityProducts
{
    public const int MaximumBatchBytes = 2 * 1024 * 1024;
    public static IReadOnlyList<string> Supported { get; } = Array.AsReadOnly(new[] { "Splunk", "QRadar", "Sentinel", "ClientBridge" });
    public static bool IsChannel(string? channel) => channel is { Length: > 0 and <= 64 }
        && char.IsAsciiLetterOrDigit(channel[0]) && channel.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static void Validate(string product, string channel, ImportSecurityAlertsRequest request)
    {
        if (!Supported.Contains(product) || !IsChannel(channel) || request is null || request.BatchId == Guid.Empty
            || !Text(request.Cursor, 2048) || request.PreviousCursor is not null && !Text(request.PreviousCursor, 2048)
            || request.Cursor == request.PreviousCursor || request.Alerts is null || request.Alerts.Count > 500)
            throw new ArgumentException("Invalid connector scope, bounded batch or advancing cursor.");
        var revisions = new HashSet<(string, DateTimeOffset)>();
        foreach (var alert in request.Alerts)
        {
            if (alert is null || !Text(alert.AlertId, 256) || !Text(alert.Title, 1024)
                || alert.Severity is not ("Critical" or "High" or "Medium" or "Low" or "Informational" or "Unknown")
                || alert.Status is not ("Open" or "Investigating" or "Closed" or "Unknown")
                || alert.UpdatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(1) || alert.UpdatedAtUtc < DateTimeOffset.UnixEpoch
                || alert.Principal is not null && !Text(alert.Principal, 2048)
                || alert.Resource is not null && !Text(alert.Resource, 4096)
                || alert.EvidenceUrl is not null && (!Uri.TryCreate(alert.EvidenceUrl, UriKind.Absolute, out var url)
                    || url.Scheme != "https" || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 || alert.EvidenceUrl.Length > 2048)
                || !revisions.Add((alert.AlertId, alert.UpdatedAtUtc.ToUniversalTime())))
                throw new ArgumentException("Invalid or duplicate alert revision. URLs must be unsigned HTTPS references without credentials or query strings.");
        }
    }

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
}
