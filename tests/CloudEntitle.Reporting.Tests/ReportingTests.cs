using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Reporting.Models;
using CloudEntitle.Reporting.Services;
using System.IO.Compression;
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

    [Theory]
    [InlineData(ReportFormat.Pdf)]
    [InlineData(ReportFormat.Excel)]
    public void BinaryFormats_ShouldProduceReadableFileStructures(ReportFormat format)
    {
        var snapshot = new ReportingSnapshot(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(),
            Array.Empty<CloudResource>(), Array.Empty<Entitlement>(), Array.Empty<EffectiveAccess>(),
            Array.Empty<RiskFindingModel>(), Array.Empty<RecommendationModel>(), Array.Empty<Scan>(), DateTime.UtcNow);

        var report = new ReportGenerator().Generate(snapshot, ReportType.Executive, format);

        Assert.NotNull(report.BinaryContent);
        if (format == ReportFormat.Pdf)
            Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(report.BinaryContent!));
        else
        {
            using var archive = new ZipArchive(new MemoryStream(report.BinaryContent!), ZipArchiveMode.Read);
            Assert.Contains(archive.Entries, entry => entry.FullName == "[Content_Types].xml");
            Assert.Contains(archive.Entries, entry => entry.FullName == "xl/worksheets/sheet1.xml");
        }
    }

    [Fact]
    public void CsvReport_ShouldBeCsvAndIncludeSelectedCategoryData()
    {
        var snapshot = new ReportingSnapshot(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(),
            Array.Empty<CloudResource>(), Array.Empty<Entitlement>(), Array.Empty<EffectiveAccess>(),
            Array.Empty<RiskFindingModel>(), Array.Empty<RecommendationModel>(), Array.Empty<Scan>(), DateTime.UtcNow)
        {
            PermissionUsageSummaries = new[]
            {
                new PermissionUsageSummary { Id = Guid.NewGuid(), WindowDays = 30, Used = 2, NotObserved = 4 }
            }
        };

        var report = new ReportGenerator().Generate(snapshot, ReportType.PermissionUsage, ReportFormat.Csv);

        Assert.StartsWith("Section,RecordJson", report.Content);
        Assert.Contains("Permission usage", report.Content);
        Assert.Contains("Used", report.Content);
        Assert.Contains("NotObserved", report.Content);
    }
}
