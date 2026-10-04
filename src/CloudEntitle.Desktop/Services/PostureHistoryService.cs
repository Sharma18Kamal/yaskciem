using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;

namespace CloudEntitle.Desktop.Services;

public sealed record PostureHistoryAnalysis(PostureTrendRecord Trend, IReadOnlyList<PostureChangeRecord> Changes);
public sealed record PostureNotificationResult(bool Configured, int Delivered, string Status);

public interface IPostureNotificationChannel
{
    Task<PostureNotificationResult> DeliverAsync(IReadOnlyList<PostureChangeRecord> alerts, CancellationToken cancellationToken = default);
}

public sealed class PostureHistoryService
{
    public static IReadOnlyList<LiveRiskFindingSnapshot> SnapshotFindings(IReadOnlyList<RiskFinding> findings)
        => findings.Select(finding => new LiveRiskFindingSnapshot(
                finding.CloudProvider.ToString(),
                string.Join('|', finding.CloudProvider, finding.CloudAccountId, finding.RuleId, finding.PrincipalId, finding.ResourceId, finding.Permission, finding.Title),
                finding.Severity.ToString(), finding.Title, finding.Principal, finding.Resource, finding.Recommendation))
            .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToArray();

    public PostureHistoryAnalysis Analyze(LiveScanHistoryEntry? previous, LiveScanHistoryEntry current)
    {
        var changes = new List<PostureChangeRecord>();
        if (previous is not null)
        {
            var providers = current.EffectiveAccess.Select(item => item.Provider)
                .Concat(current.AccountDetails.Select(item => item.Provider))
                .Concat(current.Coverage.Select(item => item.Provider))
                .Concat(current.RiskFindingDetails.Select(item => item.Provider))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var provider in providers)
            {
                CompareAccess(previous, current, provider, changes);
                CompareFindings(previous, current, provider, changes);
            }
        }

        var trend = new PostureTrendRecord(
            current.CompletedAt,
            string.Join(", ", current.AccountDetails.Select(account => account.Provider).Distinct(StringComparer.OrdinalIgnoreCase)),
            current.Accounts,
            current.Identities,
            current.Resources,
            current.Roles,
            current.Permissions,
            current.Findings,
            current.RiskFindingDetails.Count(item => item.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)),
            current.RiskFindingDetails.Count(item => item.Severity.Equals("High", StringComparison.OrdinalIgnoreCase)),
            current.EffectiveAccess.Count,
            changes.Count(item => item.Type == PostureChangeType.Added),
            changes.Count(item => item.Type == PostureChangeType.Removed),
            changes.Count(item => item.Type == PostureChangeType.Changed));
        return new PostureHistoryAnalysis(trend, changes.OrderByDescending(item => item.AlertEligible).ThenBy(item => item.Provider, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.EntityKey, StringComparer.Ordinal).ToArray());
    }

    private static void CompareAccess(LiveScanHistoryEntry previous, LiveScanHistoryEntry current, string provider, ICollection<PostureChangeRecord> changes)
    {
        var before = previous.EffectiveAccess.Where(item => item.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
            .GroupBy(AccessKey, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var after = current.EffectiveAccess.Where(item => item.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
            .GroupBy(AccessKey, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var item in after.Where(item => !before.ContainsKey(item.Key)))
        {
            var severity = item.Value.IsPrivileged ? "High" : "Informational";
            changes.Add(new PostureChangeRecord(provider, PostureChangeType.Added, "EffectiveAccess", item.Key, severity,
                $"New access observed: {item.Value.Principal} has {item.Value.Role} at {item.Value.Scope}.", "Not observed in the previous comparable scan.", FormatAccess(item.Value), current.CompletedAt, item.Value.IsPrivileged));
        }

        foreach (var item in after.Where(item => before.TryGetValue(item.Key, out var old) && HasChanged(old, item.Value)))
        {
            var old = before[item.Key];
            var escalated = !old.IsPrivileged && item.Value.IsPrivileged;
            changes.Add(new PostureChangeRecord(provider, PostureChangeType.Changed, "EffectiveAccess", item.Key, escalated ? "High" : "Informational",
                $"Access changed: {item.Value.Principal} has {item.Value.Role} at {item.Value.Scope}.", FormatAccess(old), FormatAccess(item.Value), current.CompletedAt, escalated));
        }

        if (!IsComplete(previous, provider) || !IsComplete(current, provider)) return;
        foreach (var item in before.Where(item => !after.ContainsKey(item.Key)))
        {
            changes.Add(new PostureChangeRecord(provider, PostureChangeType.Removed, "EffectiveAccess", item.Key, "Informational",
                $"Access no longer observed: {item.Value.Principal} had {item.Value.Role} at {item.Value.Scope}.", FormatAccess(item.Value), "Not observed in the current complete scan.", current.CompletedAt, false));
        }
    }

    private static void CompareFindings(LiveScanHistoryEntry previous, LiveScanHistoryEntry current, string provider, ICollection<PostureChangeRecord> changes)
    {
        var before = previous.RiskFindingDetails.Where(item => item.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Fingerprint, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var after = current.RiskFindingDetails.Where(item => item.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Fingerprint, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var item in after.Where(item => !before.ContainsKey(item.Key)))
        {
            var severity = item.Value.Severity;
            var alert = severity.Equals("Critical", StringComparison.OrdinalIgnoreCase) || severity.Equals("High", StringComparison.OrdinalIgnoreCase);
            changes.Add(new PostureChangeRecord(provider, PostureChangeType.Added, "RiskFinding", item.Key, severity,
                $"New {severity} finding: {item.Value.Title}.", "Not observed in the previous comparable scan.", item.Value.Recommendation, current.CompletedAt, alert));
        }

        foreach (var item in after.Where(item => before.TryGetValue(item.Key, out var old) && !old.Severity.Equals(item.Value.Severity, StringComparison.OrdinalIgnoreCase)))
        {
            var old = before[item.Key];
            var escalated = SeverityRank(item.Value.Severity) > SeverityRank(old.Severity);
            var alert = escalated && (item.Value.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase) || item.Value.Severity.Equals("High", StringComparison.OrdinalIgnoreCase));
            changes.Add(new PostureChangeRecord(provider, PostureChangeType.Changed, "RiskFinding", item.Key, item.Value.Severity,
                $"Finding severity changed: {item.Value.Title}.", old.Severity, item.Value.Severity, current.CompletedAt, alert));
        }
    }

    private static bool IsComplete(LiveScanHistoryEntry scan, string provider)
    {
        var sources = scan.Coverage.Where(item => item.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)
                && item.DataSource.EndsWith("Effective access", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return sources.Length > 0 && sources.All(item => item.Status is ScanCoverageStatus.Collected or ScanCoverageStatus.NoData);
    }

    private static bool HasChanged(LiveEffectiveAccessRecord before, LiveEffectiveAccessRecord after)
        => before.IsPrivileged != after.IsPrivileged
            || before.LastChangedAt != after.LastChangedAt
            || before.LastChangedBy != after.LastChangedBy
            || before.Source != after.Source;

    private static string AccessKey(LiveEffectiveAccessRecord item)
        => string.Join('|', item.Provider, item.AccountId ?? string.Empty, item.Principal, item.Role, item.Scope, item.AssignmentType);

    private static int SeverityRank(string severity) => severity.ToUpperInvariant() switch
    {
        "CRITICAL" => 4,
        "HIGH" => 3,
        "MEDIUM" => 2,
        "LOW" => 1,
        _ => 0
    };

    public static string NotificationKey(PostureChangeRecord change)
        => string.Join('|', change.Provider, change.Type, change.EntityType, change.EntityKey, change.DetectedAt.ToUniversalTime().Ticks);

    private static string FormatAccess(LiveEffectiveAccessRecord item)
        => $"{item.Role} ({item.AssignmentType}) at {item.Scope}; privileged={item.IsPrivileged}";
}

public sealed class WebhookPostureNotificationChannel : IPostureNotificationChannel
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient _httpClient;
    private readonly Func<string?> _getWebhookUrl;

    public WebhookPostureNotificationChannel() : this(SharedClient, () => Environment.GetEnvironmentVariable("YASK_CIEM_ALERT_WEBHOOK_URL")) { }

    public WebhookPostureNotificationChannel(HttpClient httpClient, Func<string?> getWebhookUrl)
    {
        _httpClient = httpClient;
        _getWebhookUrl = getWebhookUrl;
    }

    public async Task<PostureNotificationResult> DeliverAsync(IReadOnlyList<PostureChangeRecord> alerts, CancellationToken cancellationToken = default)
    {
        var eligible = alerts.Where(alert => alert.AlertEligible).ToArray();
        if (eligible.Length == 0) return new PostureNotificationResult(false, 0, "No new high-impact posture alerts.");

        var webhookUrl = _getWebhookUrl();
        if (string.IsNullOrWhiteSpace(webhookUrl)) return new PostureNotificationResult(false, 0, "Webhook not configured (YASK_CIEM_ALERT_WEBHOOK_URL); alert delivery skipped.");
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new PostureNotificationResult(true, 0, "Webhook URL must be an absolute HTTPS URL. The configured URL was not logged.");

        var payload = new
        {
            source = "YASK-CIEM",
            sentAtUtc = DateTime.UtcNow,
            text = string.Join("\n", eligible.Select(alert => $"[{alert.Severity}] {alert.Provider} {alert.Summary}")),
            alerts = eligible.Select(alert => new
            {
                provider = alert.Provider,
                change = alert.Type.ToString(),
                entityType = alert.EntityType,
                severity = alert.Severity,
                summary = alert.Summary,
                previousState = alert.PreviousState,
                currentState = alert.CurrentState,
                detectedAtUtc = alert.DetectedAt
            })
        };
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(uri, payload, cancellationToken);
            return response.IsSuccessStatusCode
                ? new PostureNotificationResult(true, eligible.Length, $"Delivered {eligible.Length} posture alert(s).")
                : new PostureNotificationResult(true, 0, $"Webhook delivery failed with HTTP {(int)response.StatusCode}; the URL and response body were not logged.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new PostureNotificationResult(true, 0, $"Webhook delivery failed ({exception.GetType().Name}); the URL was not logged.");
        }
    }
}
