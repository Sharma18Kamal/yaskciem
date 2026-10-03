using System.IO;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.Desktop.Tests;

public sealed class ScanCoverageTests
{
    [Fact]
    public async Task LiveScan_ShouldContinueOtherSourcesWhenIdentityReadIsDenied()
    {
        var historyDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var historyStore = new LocalLiveScanHistoryStore(Path.Combine(historyDirectory, "scans.jsonl"));
        var scanner = new LiveCloudScanService(new[] { new PartialCloudProvider() }, historyStore);

        var result = await scanner.ScanAsync();

        Assert.Equal(ScanCoverageStatus.Denied, Assert.Single(result.Coverage, item => item.DataSource.EndsWith("Identities", StringComparison.Ordinal)).Status);
        Assert.Contains("sub-a", Assert.Single(result.Coverage, item => item.DataSource.EndsWith("Identities", StringComparison.Ordinal)).DataSource);
        Assert.Equal(ScanCoverageStatus.NoData, Assert.Single(result.Coverage, item => item.DataSource.EndsWith("Permissions", StringComparison.Ordinal)).Status);
        Assert.Equal(ScanCoverageStatus.Collected, Assert.Single(result.Coverage, item => item.DataSource.EndsWith("Resources", StringComparison.Ordinal)).Status);
        Assert.Equal(ScanCoverageStatus.Collected, Assert.Single(result.Coverage, item => item.DataSource.EndsWith("Effective access", StringComparison.Ordinal)).Status);
        Assert.Single(result.ResourceRecords);
        Assert.Single(result.EffectiveAccessRecords);

        var saved = Assert.Single(await historyStore.GetRecentAsync());
        Assert.Equal(result.Coverage.Count, saved.Coverage.Count);
        Directory.Delete(historyDirectory, recursive: true);
    }

    [Fact]
    public async Task ScanHistory_ShouldShowLatestCoverageAndLegacyHistoryWarning()
    {
        var coverage = new[]
        {
            new ScanCoverageRecord("Azure", "subscription-a: Resources", ScanCoverageStatus.Denied, 0, "Authorization failed."),
            new ScanCoverageRecord("Azure", "subscription-a: Permissions", ScanCoverageStatus.NoData, 0, "Query succeeded with no records.")
        };
        var now = DateTime.UtcNow;
        var recent = new LiveScanHistoryEntry(now.AddMinutes(-1), now, "Azure", 1, 0, 0, 0, 0, 0, Array.Empty<LiveEffectiveAccessRecord>(), Array.Empty<LiveScanAccountRecord>()) { Coverage = coverage };
        var viewModel = new ScanHistoryViewModel(new EmptyDemoDataService(), liveScanHistoryStore: new FixedHistoryStore(new[] { recent }));

        await viewModel.RefreshAsync();

        Assert.Equal(2, viewModel.Coverage.Count);
        Assert.Contains(viewModel.Coverage, row => row.Status == nameof(ScanCoverageStatus.Denied));
        Assert.Contains("zero findings alone do not prove complete coverage", viewModel.CoverageMessage, StringComparison.OrdinalIgnoreCase);

        var legacyEntry = recent with { Coverage = Array.Empty<ScanCoverageRecord>() };
        var legacyViewModel = new ScanHistoryViewModel(new EmptyDemoDataService(), liveScanHistoryStore: new FixedHistoryStore(new[] { legacyEntry }));
        await legacyViewModel.RefreshAsync();
        Assert.Contains("predates detailed coverage", legacyViewModel.CoverageMessage, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PartialCloudProvider : ICloudProvider, IResourceProvider, ICloudEffectiveAccessProvider
    {
        private readonly Guid _accountId = Guid.NewGuid();
        public CloudProviderType ProviderType => CloudProviderType.Azure;
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudAccount>>(new[] { new CloudAccount { Id = _accountId, Name = "subscription-a", SubscriptionId = "sub-a", CloudProvider = CloudProviderType.Azure } });
        public Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Forbidden: directory read permission denied.");
        public Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudRole>>(Array.Empty<CloudRole>());
        public Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudPermission>>(Array.Empty<CloudPermission>());
        public Task<IReadOnlyList<CloudResource>> GetResourcesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudResource>>(new[] { new CloudResource { Id = Guid.NewGuid(), AccountId = accountId, Name = "vault-a", Type = "Vault", ResourceId = "/subscriptions/a/vaults/vault-a" } });
        public Task<IReadOnlyList<CloudEffectiveAccessAssignment>> DiscoverEffectiveAccessAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudEffectiveAccessAssignment>>(new[] { new CloudEffectiveAccessAssignment("owner@example.invalid", "Owner", "/subscriptions/a", "Azure RBAC", true) });
    }

    private sealed class EmptyDemoDataService : IDemoDataService
    {
        public event EventHandler? DataChanged { add { } remove { } }
        public bool IsDemoMode => false;
        public Task<DemoStatistics> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DemoStatistics(0, 0, 0, 0, 0, 0, 0, null));
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DemoSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DemoSnapshot(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(), Array.Empty<Entitlement>(), Array.Empty<DomainEffectiveAccess>(), Array.Empty<RiskFindingModel>(), Array.Empty<RecommendationModel>(), Array.Empty<Scan>()));
    }

    private sealed class FixedHistoryStore(IReadOnlyList<LiveScanHistoryEntry> entries) : ILiveScanHistoryStore
    {
        public Task RecordAsync(LiveScanHistoryEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LiveScanHistoryEntry>> GetRecentAsync(CancellationToken cancellationToken = default) => Task.FromResult(entries);
    }
}
