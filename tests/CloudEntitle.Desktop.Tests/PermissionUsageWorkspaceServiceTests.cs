using CloudEntitle.Analytics.Services;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Infrastructure.Persistence;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Recommendations.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProviderActivityEvent = CloudEntitle.Integration.Contracts.CloudActivityEvent;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class PermissionUsageWorkspaceServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_DemoDataShowsUsageTrendsAndReviewOnlyFindingsIdempotently()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestContextFactory(new DbContextOptionsBuilder<CloudEntitleDbContext>().UseSqlite(connection).Options);
        await using (var setup = await factory.CreateDbContextAsync())
            await setup.Database.EnsureCreatedAsync();
        var demo = new DemoDataService(factory);
        await demo.LoadAsync();

        var service = new PermissionUsageWorkspaceService(factory, new UnusedCollector(),
            new PermissionUsageAnalyticsService(), new PermissionUsageTrendService(),
            new ProviderActionPermissionMapper(), new CloudEntitle.RiskEngine.Services.RiskEngine(),
            new RecommendationEngine());
        var account = (await service.GetAccountsAsync(CancellationToken.None)).Single(item => item.CloudProvider == CloudProviderType.Aws);

        var first = await service.AnalyzeAsync(account.Id, 90, CancellationToken.None);
        int firstSummaryCount;
        int firstDailyCount;
        await using (var firstHistory = await factory.CreateDbContextAsync())
        {
            firstSummaryCount = await firstHistory.PermissionUsageSummaries.CountAsync(item => item.CloudAccountId == account.Id);
            firstDailyCount = await firstHistory.PermissionUsageSummaries.CountAsync(item =>
                item.CloudAccountId == account.Id && item.BucketType == "Daily");
        }
        var second = await service.AnalyzeAsync(account.Id, 90, CancellationToken.None);

        Assert.Equal(4, first.Usage.Permissions.Count);
        Assert.Equal(2, first.Usage.Permissions.Count(item =>
            item.Windows.Single(window => window.Days == 90).EvidenceState == PermissionUsageEvidenceState.Used));
        Assert.Equal(2, first.Usage.Permissions.Count(item =>
            item.Windows.Single(window => window.Days == 90).EvidenceState == PermissionUsageEvidenceState.NotObserved));
        Assert.Equal(6, first.Events.Count);
        Assert.Contains(first.Trends, item => item.BucketType == "Weekly" && item.ObservedActionCount > 0);
        Assert.Equal(2, first.Findings.Count);
        Assert.NotEmpty(first.Recommendations);
        Assert.All(first.Recommendations, item => Assert.False(item.Executable));
        Assert.Contains("Synthetic Demo Mode", first.SourceNotice);
        Assert.Equal(first.Usage.Permissions.Count, second.Usage.Permissions.Count);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(first.Usage.Permissions.Count, await verify.PermissionUsageRecords.CountAsync(item => item.CloudAccountId == account.Id));
        Assert.Equal(2, await verify.UsageAnalyticsFindings.CountAsync(item => item.CloudAccountId == account.Id));
        Assert.Equal(first.Recommendations.Count, await verify.UsageRecommendations.CountAsync(item => item.CloudAccountId == account.Id));
        Assert.Equal(firstDailyCount, await verify.PermissionUsageSummaries.CountAsync(item =>
            item.CloudAccountId == account.Id && item.BucketType == "Daily"));
        Assert.True(await verify.PermissionUsageSummaries.CountAsync(item => item.CloudAccountId == account.Id) > firstSummaryCount);
    }

    [Fact]
    public async Task CollectAndSaveAsync_DeduplicatesRepeatedProviderEventIds()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestContextFactory(new DbContextOptionsBuilder<CloudEntitleDbContext>().UseSqlite(connection).Options);
        var accountId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;
        const string externalAccountId = "123456789012";
        const string principalReference = "arn:aws:iam::123456789012:user/reader";
        var providerEvent = new ProviderActivityEvent("event-1", principalReference, "s3.amazonaws.com/GetObject",
            "s3:GetObject", null, timestamp.AddMinutes(-1), true)
        {
            PermissionEvidenceSource = "CloudTrail authorization evidence",
            PrincipalType = "User"
        };
        var snapshot = new CloudActivitySnapshot(Guid.NewGuid(), CloudProvider.Aws, externalAccountId,
            timestamp.AddDays(-1), timestamp, "CloudTrail management events", "Partial coverage", false,
            [providerEvent, providerEvent]) { CollectedAtUtc = timestamp };
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
            setup.CloudAccounts.Add(new CloudAccount
            {
                Id = accountId, Name = "AWS test account", CloudProvider = CloudProviderType.Aws,
                ExternalAccountId = externalAccountId
            });
            setup.CloudPrincipals.Add(new CloudPrincipal
            {
                Id = Guid.NewGuid(), AccountId = accountId, Name = "Reader", PrincipalId = principalReference,
                CloudProvider = CloudProviderType.Aws, IdentityType = IdentityType.User
            });
            await setup.SaveChangesAsync();
        }

        var workspace = new PermissionUsageWorkspaceService(factory, new SnapshotCollector(snapshot),
            new PermissionUsageAnalyticsService(), new PermissionUsageTrendService(),
            new ProviderActionPermissionMapper(), new CloudEntitle.RiskEngine.Services.RiskEngine(),
            new RecommendationEngine());
        await workspace.CollectAndSaveAsync(accountId, CloudActivitySource.Default, "us-east-1", "", "", "", "", CancellationToken.None);
        await workspace.CollectAndSaveAsync(accountId, CloudActivitySource.Default, "us-east-1", "", "", "", "", CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var saved = await verify.CloudActivityEvents.Where(item => item.CloudAccountId == accountId).ToArrayAsync();
        Assert.Single(saved);
        Assert.Equal(PermissionActionMappingStatus.ExactProviderEvidence, saved[0].PermissionMappingStatus);
        Assert.Equal(principalReference, saved[0].ProviderIdentity);
        Assert.Equal("Succeeded", saved[0].Outcome);
    }

    private sealed class TestContextFactory(DbContextOptions<CloudEntitleDbContext> options) : IDbContextFactory<CloudEntitleDbContext>
    {
        public CloudEntitleDbContext CreateDbContext() => new(options);
        public Task<CloudEntitleDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class UnusedCollector : ICloudActivityCollector
    {
        public Task<CloudActivitySnapshot> CollectAsync(CloudActivityQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This test analyzes persisted data without making live provider calls.");
    }

    private sealed class SnapshotCollector(CloudActivitySnapshot snapshot) : ICloudActivityCollector
    {
        public Task<CloudActivitySnapshot> CollectAsync(CloudActivityQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }
}
