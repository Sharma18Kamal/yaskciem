using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class AccessPathExplorerTests
{
    [Fact]
    public async Task Explorer_ShouldShowOnlyConnectedProviderAndUseLatestMatchingHistory()
    {
        var now = DateTime.UtcNow;
        var store = new FixedScanHistoryStore(new[]
        {
            CreateEntry(now.AddMinutes(-1), "Azure", "old-azure"),
            CreateEntry(now.AddMinutes(-3), "Gcp", "older-gcp"),
            CreateEntry(now.AddMinutes(-2), "Gcp", "latest-gcp")
        });
        var connectionState = new CloudConnectionState();
        connectionState.Update(new[] { CloudProviderType.Gcp });
        var viewModel = new AccessPathExplorerViewModel(new EmptyDemoDataService(), liveScanHistoryStore: store, cloudConnectionState: connectionState);

        await viewModel.RefreshAsync();

        Assert.Equal("Gcp", Assert.Single(viewModel.Paths).Provider);
        Assert.Equal("latest-gcp", Assert.Single(viewModel.Paths).Principal);
    }

    [Fact]
    public async Task Explorer_ShouldMarkKubernetesWorkloadIdentityAsConfiguredNotVerified()
    {
        var record = new LiveEffectiveAccessRecord(
            "AWS", "arn:aws:iam::123456789012:role/workload", "ClusterRole/viewer", "clusters/test",
            "Kubernetes workload identity via serviceaccount:apps:backend", false, "ServiceIdentity");
        var history = new FixedScanHistoryStore(new[]
        {
            new LiveScanHistoryEntry(DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow, "AWS", 1, 1, 0, 1, 1, 0,
                new[] { record }, Array.Empty<LiveScanAccountRecord>())
        });
        var viewModel = new AccessPathExplorerViewModel(new EmptyDemoDataService(), liveScanHistoryStore: history);
        await viewModel.RefreshAsync();

        var path = Assert.Single(viewModel.Paths);

        Assert.Contains(path.Nodes, node => node.Type == "Kubernetes ServiceAccount" && node.Name == "serviceaccount:apps:backend");
        Assert.Contains(path.Edges, edge => edge.Confidence.Contains("not verified", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Explorer_ShouldVisualizePathAndEstimateObservedBlastRadius()
    {
        var now = DateTime.UtcNow;
        var access = new[]
        {
            new LiveEffectiveAccessRecord("Azure", "user-a", "Owner", "/subscriptions/sub-a/resourceGroups/rg-a", "Azure RBAC", true, AccountId: "sub-a"),
            new LiveEffectiveAccessRecord("Azure", "user-a", "Reader", "/subscriptions/sub-a/resourceGroups/rg-a", "Azure RBAC", false, AccountId: "sub-a"),
            new LiveEffectiveAccessRecord("Azure", "user-b", "Reader", "/subscriptions/sub-b", "Azure RBAC", false, AccountId: "sub-b")
        };
        var resources = new[]
        {
            new CloudResource { Id = Guid.NewGuid(), Name = "vault-a", ResourceId = "/subscriptions/sub-a/resourceGroups/rg-a/providers/Microsoft.KeyVault/vaults/vault-a" },
            new CloudResource { Id = Guid.NewGuid(), Name = "vault-b", ResourceId = "/subscriptions/sub-b/resourceGroups/rg-b/providers/Microsoft.KeyVault/vaults/vault-b" }
        };
        var result = new LiveCloudScanResult(2, 2, 2, 2, 2, 1, Array.Empty<RiskFinding>(), now, Array.Empty<string>(),
            Array.Empty<CloudPrincipal>(), resources, Array.Empty<DiscoveredCloudRole>(), Array.Empty<DiscoveredCloudPermission>(),
            now.AddMinutes(-1), access, Array.Empty<LiveScanAccountRecord>());
        var viewModel = new AccessPathExplorerViewModel(new EmptyDemoDataService(), liveCloudScanService: new FixedLiveScanService(result));

        await viewModel.RefreshAsync();

        var path = Assert.Single(viewModel.Paths, item => item.Principal == "user-a" && item.Role == "Owner");
        Assert.Equal(3, viewModel.SelectedVisualNodes.Count);
        Assert.Equal("Granted role", viewModel.SelectedVisualNodes[0].Relationship);
        Assert.Equal(2, viewModel.BlastRadius!.RelatedAssignments);
        Assert.Equal(2, viewModel.BlastRadius.RelatedRoles);
        Assert.Equal(1, viewModel.BlastRadius.ObservedResources);
        Assert.Contains("lower bound", viewModel.BlastRadius.Caveat, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Owner", path.Role);
    }

    private static LiveScanHistoryEntry CreateEntry(DateTime completedAt, string provider, string principal)
        => new(completedAt.AddMinutes(-1), completedAt, provider, 1, 1, 0, 1, 1, 0,
            new[] { new LiveEffectiveAccessRecord(provider, principal, "Viewer", "scope", "test source", false) },
            Array.Empty<LiveScanAccountRecord>());

    private sealed class EmptyDemoDataService : IDemoDataService
    {
        public event EventHandler? DataChanged { add { } remove { } }
        public bool IsDemoMode => false;
        public Task<DemoStatistics> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DemoStatistics(0, 0, 0, 0, 0, 0, 0, null));
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DemoSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DemoSnapshot(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(), Array.Empty<Entitlement>(), Array.Empty<CloudEntitle.Domain.Entities.EffectiveAccess>(), Array.Empty<RiskFindingModel>(), Array.Empty<RecommendationModel>(), Array.Empty<Scan>()));
    }

    private sealed class FixedScanHistoryStore(IReadOnlyList<LiveScanHistoryEntry> entries) : ILiveScanHistoryStore
    {
        public Task RecordAsync(LiveScanHistoryEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LiveScanHistoryEntry>> GetRecentAsync(CancellationToken cancellationToken = default) => Task.FromResult(entries);
    }

    private sealed class FixedLiveScanService : ILiveCloudScanService
    {
        private readonly LiveCloudScanResult _result;

        public FixedLiveScanService(LiveCloudScanResult result) => _result = result;

        public LiveCloudScanResult? CurrentResult => _result;
        public event EventHandler? ScanCompleted { add { } remove { } }
        public Task<LiveCloudScanResult> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult(_result);
    }
}
