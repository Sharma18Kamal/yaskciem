using System.Text;
using System.Text.Json;
using CloudEntitle.Reporting.Models;

namespace CloudEntitle.Reporting.Services;

public sealed class ReportGenerator
{
    public GeneratedReport Generate(ReportingSnapshot snapshot, ReportType type, ReportFormat format)
    {
        object payload;
        switch (type)
        {
            case ReportType.Executive: payload = new { type, snapshot.GeneratedAt, Accounts = snapshot.Accounts.Count, Identities = snapshot.Principals.Count, PrivilegedIdentities = snapshot.Principals.Count(item => item.IsPrivileged), CriticalFindings = snapshot.Findings.Count(item => item.Severity == CloudEntitle.Domain.RiskSeverity.Critical), HighFindings = snapshot.Findings.Count(item => item.Severity == CloudEntitle.Domain.RiskSeverity.High), AccessDecisions = snapshot.EffectiveAccess.GroupBy(item => item.Decision).ToDictionary(group => group.Key.ToString(), group => group.Count()) }; break;
            case ReportType.Technical: payload = new { type, snapshot.GeneratedAt, Accounts = snapshot.Accounts, Principals = snapshot.Principals, Resources = snapshot.Resources, Entitlements = snapshot.Entitlements, EffectiveAccess = snapshot.EffectiveAccess }; break;
            case ReportType.Identity: payload = new { type, snapshot.GeneratedAt, Identities = snapshot.Principals }; break;
            case ReportType.Entitlement: payload = new { type, snapshot.GeneratedAt, Entitlements = snapshot.Entitlements, EffectiveAccess = snapshot.EffectiveAccess }; break;
            case ReportType.Risk: payload = new { type, snapshot.GeneratedAt, Findings = snapshot.Findings }; break;
            default: payload = new { type, snapshot.GeneratedAt, Recommendations = snapshot.Recommendations }; break;
        }
        var content = format switch
        {
            ReportFormat.Json => JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            ReportFormat.Html => $"<html><head><title>{type} Report</title></head><body><h1>{type} Report</h1><pre>{System.Net.WebUtility.HtmlEncode(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }))}</pre></body></html>",
            _ => JsonSerializer.Serialize(payload)
        };
        return new GeneratedReport(type, format, content, snapshot.GeneratedAt);
    }
}

public sealed class ReportScheduler
{
    public IReadOnlyList<ScheduledReport> GetDueReports(IEnumerable<ScheduledReport> schedules, DateTime now) => schedules.Where(schedule => schedule.Enabled && schedule.NextRunAt <= now).OrderBy(schedule => schedule.NextRunAt).ToArray();

    public ScheduledReport Advance(ScheduledReport schedule)
    {
        var next = schedule.Schedule switch { ReportSchedule.Daily => schedule.NextRunAt.AddDays(1), ReportSchedule.Weekly => schedule.NextRunAt.AddDays(7), _ => schedule.NextRunAt.AddMonths(1) };
        return schedule with { NextRunAt = next };
    }
}
