using CloudEntitle.Analytics.Models;
using CloudEntitle.Analytics.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.RiskEngine.Models;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.Analytics.Tests;

public sealed class PermissionUsageAnalyticsTests
{
    [Fact]
    public void Calculate_ReportsUsageWindowsFrequenciesAndLastUsedTime()
    {
        var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        var access = CreateAccess();
        var permission = new CloudPermission { Id = access.PermissionId, Name = "Read objects", Action = "s3:GetObject" };
        var actions = new[]
        {
            Activity(access, "s3:GetObject", now.AddDays(-10)),
            Activity(access, "s3:GetObject", now.AddDays(-45)),
            Activity(access, "s3:GetObject", now.AddDays(-75))
        };
        var coverage = new[]
        {
            new ActivityEvidenceCoverage(access.CloudAccountId, access.Provider, now.AddDays(-90), now,
                ActivityCoverageStatus.Complete, true, true, true, actions.Length)
        };

        var report = new PermissionUsageAnalyticsService().Calculate(
            new[] { access }, new[] { permission }, actions, coverage, now);

        var usage = Assert.Single(report.Permissions);
        Assert.Equal(now.AddDays(-10), usage.LastUsedAtUtc);
        Assert.Equal(PermissionUsageState.Used, Assert.Single(usage.Windows, item => item.Days == 30).State);
        Assert.Equal(1, Assert.Single(usage.Windows, item => item.Days == 30).SuccessfulActionCount);
        Assert.Equal(PermissionUsageState.Used, Assert.Single(usage.Windows, item => item.Days == 60).State);
        Assert.Equal(2, Assert.Single(usage.Windows, item => item.Days == 60).SuccessfulActionCount);
        Assert.Equal(PermissionUsageState.Used, Assert.Single(usage.Windows, item => item.Days == 90).State);
        Assert.Equal(3, Assert.Single(usage.Windows, item => item.Days == 90).SuccessfulActionCount);
        Assert.Equal(3, Assert.Single(usage.ActionFrequencies).SuccessfulActionCount);
        Assert.All(report.Summary, summary => Assert.Equal(1, summary.Used));
    }

    [Fact]
    public void Calculate_ReportsUnusedOnlyWithCompleteAttributionCoverage()
    {
        var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        var access = CreateAccess();
        var permission = new CloudPermission { Id = access.PermissionId, Name = "Read objects", Action = "s3:GetObject" };
        var completeCoverage = new[]
        {
            new ActivityEvidenceCoverage(access.CloudAccountId, access.Provider, now.AddDays(-90), now,
                ActivityCoverageStatus.Complete, true, true, true, 0)
        };
        var partialCoverage = new[]
        {
            new ActivityEvidenceCoverage(access.CloudAccountId, access.Provider, now.AddDays(-90), now,
                ActivityCoverageStatus.Partial, true, true, true, 0)
        };

        var service = new PermissionUsageAnalyticsService();
        var complete = service.Calculate(new[] { access }, new[] { permission }, Array.Empty<ActivityRecord>(), completeCoverage, now);
        var partial = service.Calculate(new[] { access }, new[] { permission }, Array.Empty<ActivityRecord>(), partialCoverage, now);

        Assert.All(Assert.Single(complete.Permissions).Windows, window => Assert.Equal(PermissionUsageState.Unused, window.State));
        Assert.All(Assert.Single(partial.Permissions).Windows, window => Assert.Equal(PermissionUsageState.Unknown, window.State));
    }

    [Fact]
    public void Calculate_DoesNotAttributeFailedOrMismatchedActions()
    {
        var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        var access = CreateAccess();
        var permission = new CloudPermission { Id = access.PermissionId, Name = "Read objects", Action = "s3:GetObject" };
        var actions = new[]
        {
            new ActivityRecord
            {
                Id = Guid.NewGuid(),
                CloudAccountId = access.CloudAccountId,
                PrincipalId = access.PrincipalId,
                RoleId = access.RoleId,
                ResourceId = access.ResourceId,
                Action = "s3:GetObject",
                Timestamp = now.AddDays(-1),
                Success = false
            },
            Activity(access, "s3:ListBucket", now.AddDays(-1))
        };
        var coverage = new[]
        {
            new ActivityEvidenceCoverage(access.CloudAccountId, access.Provider, now.AddDays(-90), now,
                ActivityCoverageStatus.Complete, true, true, true, actions.Length)
        };

        var report = new PermissionUsageAnalyticsService().Calculate(
            new[] { access }, new[] { permission }, actions, coverage, now);

        var usage = Assert.Single(report.Permissions);
        Assert.Null(usage.LastUsedAtUtc);
        Assert.All(usage.Windows, window => Assert.Equal(PermissionUsageState.Unused, window.State));
    }

    private static DomainEffectiveAccess CreateAccess()
        => new()
        {
            Id = Guid.NewGuid(),
            CloudAccountId = Guid.NewGuid(),
            Provider = CloudProvider.Aws,
            PrincipalId = Guid.NewGuid(),
            PermissionId = Guid.NewGuid(),
            RoleId = Guid.NewGuid(),
            ResourceId = Guid.NewGuid(),
            Decision = AccessDecision.Allow
        };

    private static ActivityRecord Activity(DomainEffectiveAccess access, string action, DateTime timestamp)
        => new()
        {
            Id = Guid.NewGuid(),
            CloudAccountId = access.CloudAccountId,
            PrincipalId = access.PrincipalId,
            RoleId = access.RoleId,
            ResourceId = access.ResourceId,
            Action = action,
            Timestamp = timestamp,
            Success = true
        };
}
