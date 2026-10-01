using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Reporting.Models;
using CloudEntitle.Reporting.Services;
using Xunit;

namespace CloudEntitle.Reporting.Tests;

public sealed class ReportingTests
{
    [Fact]
    public void ExecutiveReport_ShouldContainPostureMetrics()
    {
        var snapshot = new ReportingSnapshot(new[] { new CloudAccount { Id = Guid.NewGuid(), Name = "Azure", CloudProvider = CloudProviderType.Azure } }, Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(), Array.Empty<Entitlement>(), Array.Empty<EffectiveAccess>(), Array.Empty<RiskFindingModel>(), Array.Empty<RecommendationModel>(), Array.Empty<Scan>(), DateTime.UtcNow);

        var report = new ReportGenerator().Generate(snapshot, ReportType.Executive, ReportFormat.Json);

        Assert.Contains("Accounts", report.Content);
        Assert.Contains("PrivilegedIdentities", report.Content);
    }

    [Fact]
    public void HtmlReport_ShouldEncodeGeneratedContent()
    {
        var snapshot = new ReportingSnapshot(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(), Array.Empty<Entitlement>(), Array.Empty<EffectiveAccess>(), Array.Empty<RiskFindingModel>(), Array.Empty<RecommendationModel>(), Array.Empty<Scan>(), DateTime.UtcNow);

        var report = new ReportGenerator().Generate(snapshot, ReportType.Risk, ReportFormat.Html);

        Assert.StartsWith("<html>", report.Content);
        Assert.Contains("Risk Report", report.Content);
    }

    [Fact]
    public void Scheduler_ShouldReturnDueReportsAndAdvanceCadence()
    {
        var now = DateTime.UtcNow;
        var schedule = new ScheduledReport(Guid.NewGuid(), "Daily risk", ReportType.Risk, ReportFormat.Json, ReportSchedule.Daily, now.AddMinutes(-1), true);
        var scheduler = new ReportScheduler();

        Assert.Single(scheduler.GetDueReports(new[] { schedule }, now));
        Assert.Equal(schedule.NextRunAt.AddDays(1), scheduler.Advance(schedule).NextRunAt);
    }
}
