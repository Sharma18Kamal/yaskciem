using CloudEntitle.Analytics.Models;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;

namespace CloudEntitle.Analytics.Services;

public interface IAnalyticsService
{
    AnalyticsSnapshot Calculate(DemoSnapshot snapshot, DateTime? asOf = null);
}

public sealed class AnalyticsService : IAnalyticsService
{
    public AnalyticsSnapshot Calculate(DemoSnapshot snapshot, DateTime? asOf = null)
    {
        var now = asOf ?? DateTime.UtcNow;
        var dormant = snapshot.Principals.Count(principal => principal.LastActivityAt.HasValue && (now - principal.LastActivityAt.Value).TotalDays >= 90);
        return new AnalyticsSnapshot(
            snapshot.Accounts.Count,
            snapshot.Principals.Count,
            snapshot.Resources.Count,
            snapshot.Entitlements.Count,
            snapshot.Principals.Count(principal => principal.IsPrivileged),
            snapshot.RiskFindings.Count,
            snapshot.RiskFindings.Count(finding => finding.Severity == RiskSeverity.Critical),
            snapshot.RiskFindings.Count(finding => finding.Severity == RiskSeverity.High),
            dormant,
            snapshot.Principals.Count(principal => principal.IsExternal),
            snapshot.EffectiveAccess.Count(access => access.Decision == AccessDecision.Allow),
            snapshot.EffectiveAccess.Count(access => access.Decision == AccessDecision.Deny),
            snapshot.EffectiveAccess.Count(access => access.Decision == AccessDecision.Conditional),
            snapshot.Scans.OrderByDescending(scan => scan.CompletedAt).FirstOrDefault()?.CompletedAt,
            Metrics(snapshot.Accounts.GroupBy(account => account.CloudProvider).Select(group => (group.Key.ToString(), group.Count())), "No accounts"),
            Metrics(snapshot.RiskFindings.GroupBy(finding => finding.CloudProvider).Select(group => (group.Key.ToString(), group.Count())), "No findings"),
            Metrics(snapshot.RiskFindings.GroupBy(finding => finding.Severity).Select(group => (group.Key.ToString(), group.Count())), "No findings"),
            Metrics(snapshot.Principals.GroupBy(principal => principal.IdentityType).Select(group => (group.Key.ToString(), group.Count())), "No identities"),
            Metrics(snapshot.EffectiveAccess.GroupBy(access => access.Decision).Select(group => (group.Key.ToString(), group.Count())), "No access decisions"));
    }

    private static IReadOnlyList<AnalyticsMetric> Metrics(IEnumerable<(string Label, int Value)> values, string emptyLabel)
    {
        var metrics = values.OrderByDescending(value => value.Value).ThenBy(value => value.Label).Select(value => new AnalyticsMetric(value.Label, value.Value)).ToArray();
        return metrics.Length == 0 ? new[] { new AnalyticsMetric(emptyLabel, 0) } : metrics;
    }
}
