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
}
