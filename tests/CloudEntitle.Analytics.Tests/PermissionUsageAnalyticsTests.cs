using CloudEntitle.Analytics.Models;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.RiskEngine.Models;
using CloudEntitle.Analytics.Services;
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

    [Fact]
    public void Calculate_MissingActionsOrAttributionMustNotClassifyPermissionsUnused()
    {
        var now = DateTime.UtcNow;
        var access = CreateAccess();
        var permission = new CloudPermission { Id = access.PermissionId, Name = "Read", Action = "" };
        var coverage = new[] { new ActivityEvidenceCoverage(access.CloudAccountId, access.Provider, now.AddDays(-90), now,
            ActivityCoverageStatus.Complete, true, true, true, 0) };
        var service = new PermissionUsageAnalyticsService();
        var result = service.Calculate(new[] { access }, new[] { permission }, Array.Empty<ActivityRecord>(), coverage, now);
        Assert.All(Assert.Single(result.Permissions).Windows, item => Assert.Equal(PermissionUsageState.Unknown, item.State));
        permission.Action = "s3:GetObject";
        var unattributed = Activity(access, permission.Action, now.AddDays(-1));
        unattributed.RoleId = null;
        result = service.Calculate(new[] { access }, new[] { permission }, new[] { unattributed }, coverage, now);
        Assert.All(Assert.Single(result.Permissions).Windows, item => Assert.Equal(PermissionUsageState.Used, item.State));
        Assert.Contains("does not identify which role or policy granted it", Assert.Single(result.Permissions).CorrelationStatus);
        Assert.Throws<ArgumentException>(() => service.Calculate(new[] { access }, Array.Empty<CloudPermission>(), Array.Empty<ActivityRecord>(), coverage, now));
    }

    private static ActivityRecord Activity(DomainEffectiveAccess access, string action, DateTime timestamp)
        => new()
        {
            Id = Guid.NewGuid(),
            CloudAccountId = access.CloudAccountId,
            PrincipalId = access.PrincipalId,
            RoleId = access.RoleId,
            ResourceId = access.ResourceId,
            Action = action,
            PermissionAction = action,
            PermissionMappingStatus = PermissionActionMappingStatus.ExactProviderEvidence,
            PermissionMappingEvidence = "Test provider authorization evidence",
            Timestamp = timestamp,
            Success = true
        };

    [Fact]
    public void Calculate_DistinguishesUsageStatesAndAvoidsGuessingOperationMappings()
    {
        var now = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
        var access = CreateAccess();
        var permission = new CloudPermission { Id = access.PermissionId, Name = "Read objects", Action = "s3:GetObject" };
        var candidates = new[]
        {
            new ActivityRecord { Id = Guid.NewGuid(), CloudAccountId = access.CloudAccountId, PrincipalId = access.PrincipalId,
                ResourceId = access.ResourceId, Action = "s3:GetObject", PermissionAction = "s3:GetObject", PermissionMappingStatus = PermissionActionMappingStatus.CuratedCandidate,
                Timestamp = now.AddDays(-1), Success = true }
        };
        var partial = new[] { new ActivityEvidenceCoverage(access.CloudAccountId, access.Provider, now.AddDays(-90), now,
            ActivityCoverageStatus.Partial, true, true, false, 1) };
        var service = new PermissionUsageAnalyticsService();
        var unknownMapping = service.Calculate(new[] { access }, new[] { permission }, candidates, partial, now);
        Assert.All(Assert.Single(unknownMapping.Permissions).Windows,
            item => Assert.Equal(PermissionUsageEvidenceState.UnknownMapping, item.EvidenceState));
        Assert.Equal(1, Assert.Single(unknownMapping.Permissions).CandidateActionCount90Days);

        var noCoverage = service.Calculate(new[] { access }, new[] { permission }, Array.Empty<ActivityRecord>(), Array.Empty<ActivityEvidenceCoverage>(), now);
        Assert.All(Assert.Single(noCoverage.Permissions).Windows,
            item => Assert.Equal(PermissionUsageEvidenceState.InsufficientObservation, item.EvidenceState));

        var incomplete = service.Calculate(new[] { access }, new[] { permission }, Array.Empty<ActivityRecord>(), partial, now);
        Assert.All(Assert.Single(incomplete.Permissions).Windows,
            item => Assert.Equal(PermissionUsageEvidenceState.CollectionIncomplete, item.EvidenceState));
    }

    [Fact]
    public void ProviderActionMapper_UsesOnlyExplicitAuthorizationEvidenceOrReviewedCandidates()
    {
        var mapper = new ProviderActionPermissionMapper();
        Assert.Equal(PermissionActionMappingStatus.ExactProviderEvidence,
            mapper.Map(CloudProvider.Gcp, "storage.objects.get", "storage.objects.get", "authorizationInfo.permission").Status);
        var aws = mapper.Map(CloudProvider.Aws, "s3.amazonaws.com/GetObject", null, null);
        Assert.Equal(PermissionActionMappingStatus.CuratedCandidate, aws.Status);
        Assert.Equal("s3:GetObject", aws.PermissionAction);
        Assert.Equal(PermissionActionMappingStatus.ExactProviderEvidence,
            mapper.Map(CloudProvider.Aws, "s3.amazonaws.com/GetObject", "s3:GetObject", "CloudTrail authorization evidence").Status);
        Assert.Equal(PermissionActionMappingStatus.ExactProviderEvidence,
            mapper.Map(CloudProvider.Azure, "GetBlob", "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read", "authorization.action").Status);
        Assert.Equal(PermissionActionMappingStatus.Unknown,
            mapper.Map(CloudProvider.Gcp, "storage.objects.get", null, null).Status);
    }

    [Fact]
    public void PermissionUsageTrends_AggregateDailyWeeklyMonthlyByIdentityAndProvider()
    {
        var account = Guid.NewGuid();
        var identity = Guid.NewGuid();
        var time = new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            new CloudActivityEvent { Id = Guid.NewGuid(), Provider = CloudProvider.Azure, CloudAccountId = account,
                IdentityId = identity, EventTimestamp = time, Outcome = "Succeeded",
                OutcomeKnown = true, PermissionMappingStatus = PermissionActionMappingStatus.ExactProviderEvidence },
            new CloudActivityEvent { Id = Guid.NewGuid(), Provider = CloudProvider.Azure, CloudAccountId = account,
                IdentityId = identity, EventTimestamp = time.AddHours(1), Outcome = "Succeeded",
                PermissionMappingStatus = PermissionActionMappingStatus.CuratedCandidate }
        };
        var result = new PermissionUsageTrendService().Calculate(events);
        Assert.Equal(3, result.Count);
        Assert.All(result, item => { Assert.Equal(identity, item.IdentityId); Assert.Equal(1, item.ObservedActionCount); Assert.Equal(1, item.CandidateActionCount); });
        Assert.Contains(result, item => item.BucketType == "Weekly" && item.BucketStartUtc.DayOfWeek == DayOfWeek.Monday);
        Assert.Contains(result, item => item.BucketType == "Monthly" && item.BucketStartUtc.Day == 1);
    }
}
