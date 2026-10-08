using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Recommendations.Models;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.Desktop.Tests;

public sealed class UsageBasedRecommendationTests
{
    [Fact]
    public async Task LiveScan_ShouldPropagateProviderRoleUsageAndActionCoverage()
    {
        var accountId = Guid.NewGuid();
        var provider = new FakeAwsUsageProvider(accountId);
        var result = await new LiveCloudScanService(new ICloudProvider[] { provider }).ScanAsync();

        var roleUsage = Assert.Single(result.EffectiveAccessRecords, item => item.AssignmentType == "Role usage");
        Assert.True(roleUsage.UsageHistoryComplete);
        Assert.Equal(400, roleUsage.UsageHistoryDays);
        Assert.Equal("AWS IAM ListRoles.RoleLastUsed", roleUsage.UsageEvidenceSource);
        Assert.Contains(result.Coverage, item => item.DataSource.EndsWith(": AWS IAM role last-used", StringComparison.Ordinal)
            && item.Status == ScanCoverageStatus.Collected);
        Assert.Contains(result.Coverage, item => item.DataSource.EndsWith(": AWS action-level usage", StringComparison.Ordinal)
            && item.Status == ScanCoverageStatus.Unsupported);
    }

    [Fact]
    public async Task CompleteAwsRoleLastUsedEvidence_ShouldCreateProviderSpecificReviewRecommendation()
    {
        var completedAt = DateTime.UtcNow;
        var scan = CreateScan(completedAt, lastUsedAt: null, historyComplete: true, coverageStatus: ScanCoverageStatus.Collected);
        var viewModel = CreateViewModel(scan);

        await viewModel.RefreshAsync();

        var recommendation = Assert.Single(viewModel.Recommendations, row => row.Model?.RecommendationType == RecommendationType.RemoveUnusedRole);
        Assert.Equal("AWS", recommendation.Provider);
        Assert.Equal("Confirmed", recommendation.State);
        Assert.Contains("UnusedRole", recommendation.Current, StringComparison.Ordinal);
        Assert.Contains("ReadOnlyAccess", recommendation.Current, StringComparison.Ordinal);
        Assert.Contains("RoleLastUsed", recommendation.Evidence, StringComparison.Ordinal);
        Assert.Contains("400", recommendation.Evidence, StringComparison.Ordinal);
        Assert.Contains("Review", recommendation.Suggested, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ScanCoverageStatus.Partial, false, 120)]
    [InlineData(ScanCoverageStatus.Collected, false, 120)]
    [InlineData(ScanCoverageStatus.Collected, true, 30)]
    public async Task IncompleteOrRecentUsageEvidence_ShouldNotRecommendRoleRemoval(
        ScanCoverageStatus coverageStatus, bool historyComplete, int? lastUsedDaysAgo)
    {
        var completedAt = DateTime.UtcNow;
        DateTime? lastUsedAt = lastUsedDaysAgo.HasValue ? completedAt.AddDays(-lastUsedDaysAgo.Value) : null;
        var scan = CreateScan(completedAt, lastUsedAt, historyComplete, coverageStatus);
        var viewModel = CreateViewModel(scan);

        await viewModel.RefreshAsync();

        Assert.DoesNotContain(viewModel.Recommendations, row => row.Model?.RecommendationType == RecommendationType.RemoveUnusedRole);
    }

    private static RecommendationsViewModel CreateViewModel(LiveCloudScanResult scan)
        => new(new EmptyDemoDataService(), new EmptyRecommendationStore(), new FakeLiveScanService(scan));

    private static LiveCloudScanResult CreateScan(DateTime completedAt, DateTime? lastUsedAt, bool historyComplete, ScanCoverageStatus coverageStatus)
    {
        const string accountId = "123456789012";
        var roleUsage = new LiveEffectiveAccessRecord(
            "Aws", "UnusedRole", "UnusedRole", "AWS account", "AWS IAM RoleLastUsed", true,
            "Role usage", lastUsedAt, AccountId: accountId, UsageHistoryComplete: historyComplete,
            UsageHistoryDays: 400, UsageEvidenceSource: "AWS IAM ListRoles.RoleLastUsed");
        var grant = new LiveEffectiveAccessRecord(
            "Aws", "UnusedRole", "ReadOnlyAccess", "AWS account", "AWS IAM role policy", false,
            AccountId: accountId);
        return new LiveCloudScanResult(
            1, 1, 1, 1, 1, 0, Array.Empty<RiskFinding>(), completedAt, Array.Empty<string>(),
            Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(), Array.Empty<DiscoveredCloudRole>(),
            Array.Empty<DiscoveredCloudPermission>(), completedAt, new[] { roleUsage, grant },
            new[] { new LiveScanAccountRecord("Aws", "Production", accountId, "", "", "Scanned") })
        {
            Coverage = new[]
            {
                new ScanCoverageRecord("Aws", "Production (123456789012): AWS IAM role last-used", coverageStatus, 1,
                    "AWS IAM RoleLastUsed; role-level activity only, maximum 400-day history.")
            }
        };
    }

    private sealed class EmptyRecommendationStore : IRecommendationStore
    {
        public Task<IReadOnlyList<RecommendationModel>> GetAsync(string? search = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RecommendationModel>>(Array.Empty<RecommendationModel>());
    }

    private sealed class FakeLiveScanService(LiveCloudScanResult result) : ILiveCloudScanService
    {
        public LiveCloudScanResult? CurrentResult { get; } = result;
        public event EventHandler? ScanCompleted { add { } remove { } }
        public Task<IReadOnlyList<LiveScanAccountRecord>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CurrentResult!.AccountRecords);
        public Task<LiveCloudScanResult> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult(CurrentResult!);
        public Task<LiveCloudScanResult> ScanAsync(IReadOnlySet<string>? selectedAccountKeys, CancellationToken cancellationToken = default)
            => Task.FromResult(CurrentResult!);
    }

    private sealed class FakeAwsUsageProvider(Guid accountId) : ICloudProvider, IResourceProvider, ICloudEffectiveAccessProvider
    {
        public CloudProviderType ProviderType => CloudProviderType.Aws;
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudAccount>>(new[] { new CloudAccount { Id = accountId, Name = "AWS account", CloudProvider = CloudProviderType.Aws, ExternalAccountId = "123456789012" } });
        public Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudPrincipal>>(Array.Empty<CloudPrincipal>());
        public Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudRole>>(Array.Empty<CloudRole>());
        public Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudPermission>>(Array.Empty<CloudPermission>());
        public Task<IReadOnlyList<CloudResource>> GetResourcesAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudResource>>(Array.Empty<CloudResource>());
        public Task<IReadOnlyList<CloudEffectiveAccessAssignment>> DiscoverEffectiveAccessAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudEffectiveAccessAssignment>>(new[]
            {
                new CloudEffectiveAccessAssignment("UnusedRole", "UnusedRole", "AWS account", "AWS IAM RoleLastUsed", false, "Role usage",
                    UsageHistoryComplete: true, UsageHistoryDays: 400, UsageEvidenceSource: "AWS IAM ListRoles.RoleLastUsed")
            });
    }

    private sealed class EmptyDemoDataService : IDemoDataService
    {
        public event EventHandler? DataChanged { add { } remove { } }
        public bool IsDemoMode => false;
        public Task<DemoStatistics> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DemoStatistics(0, 0, 0, 0, 0, 0, 0, null));
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DemoSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DemoSnapshot(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(),
                Array.Empty<Entitlement>(), Array.Empty<DomainEffectiveAccess>(), Array.Empty<RiskFindingModel>(),
                Array.Empty<RecommendationModel>(), Array.Empty<Scan>()));
    }
}
