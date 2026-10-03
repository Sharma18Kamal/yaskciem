using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.Desktop.Tests;

public sealed class AccessChangeTimelineTests
{
    [Fact]
    public async Task Timeline_ShouldCompareAssignmentsAndPreserveAuditAttribution()
    {
        var beforeTime = DateTime.UtcNow.AddHours(-2);
        var afterTime = DateTime.UtcNow.AddHours(-1);
        var auditTime = DateTime.UtcNow.AddMinutes(-30);
        var previous = new LiveScanHistoryEntry(beforeTime.AddMinutes(-1), beforeTime, "Azure", 1, 2, 0, 1, 1, 0,
            new[]
            {
                new LiveEffectiveAccessRecord("Azure", "removed-user", "Reader", "subscription", "Azure RBAC", false),
                new LiveEffectiveAccessRecord("Azure", "changed-user", "Contributor", "resource-group", "Azure RBAC", false, LastChangedAt: beforeTime.AddDays(-1), LastChangedBy: "previous-actor")
            }, Array.Empty<LiveScanAccountRecord>());
        var current = new LiveScanHistoryEntry(afterTime.AddMinutes(-1), afterTime, "Azure", 1, 2, 0, 1, 2, 0,
            new[]
            {
                new LiveEffectiveAccessRecord("Azure", "changed-user", "Contributor", "resource-group", "Azure RBAC; Activity Log", true, LastChangedAt: auditTime, LastChangedBy: "audit@example.invalid"),
                new LiveEffectiveAccessRecord("Azure", "added-user", "Reader", "subscription", "Azure RBAC", false)
            }, Array.Empty<LiveScanAccountRecord>());
        var viewModel = new ScanHistoryViewModel(new EmptyDemoDataService(), liveScanHistoryStore: new FixedScanHistoryStore(new[] { current, previous }));

        await viewModel.RefreshAsync();

        Assert.Equal(3, viewModel.AccessChanges.Count);
        var changed = Assert.Single(viewModel.AccessChanges, row => row.Change == "Changed");
        Assert.Equal(afterTime, changed.DetectedAt);
        Assert.Equal(auditTime, changed.ProviderChangedAt);
        Assert.Equal("audit@example.invalid", changed.ChangedBy);
        Assert.Equal("Azure RBAC; Activity Log", changed.Source);
        Assert.Contains(viewModel.AccessChanges, row => row.Change == "Added" && row.Principal == "added-user");
        Assert.Contains(viewModel.AccessChanges, row => row.Change == "Removed" && row.Principal == "removed-user");
        Assert.Contains("seven days", viewModel.AccessChangeCoverageMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Timeline_ShouldNotInferRemovalsFromAnEmptyProviderResult()
    {
        var now = DateTime.UtcNow;
        var previous = new LiveScanHistoryEntry(now.AddMinutes(-3), now.AddMinutes(-2), "GCP", 1, 1, 0, 1, 1, 0,
            new[] { new LiveEffectiveAccessRecord("Gcp", "user@example.invalid", "roles/viewer", "projects/demo", "GCP IAM", false) },
            Array.Empty<LiveScanAccountRecord>());
        var empty = new LiveScanHistoryEntry(now.AddMinutes(-1), now, "GCP", 1, 0, 0, 0, 0, 0,
            Array.Empty<LiveEffectiveAccessRecord>(), Array.Empty<LiveScanAccountRecord>());
        var viewModel = new ScanHistoryViewModel(new EmptyDemoDataService(), liveScanHistoryStore: new FixedScanHistoryStore(new[] { previous, empty }));

        await viewModel.RefreshAsync();

        Assert.Empty(viewModel.AccessChanges);
    }

    private sealed class FixedScanHistoryStore(IReadOnlyList<LiveScanHistoryEntry> entries) : ILiveScanHistoryStore
    {
        public Task RecordAsync(LiveScanHistoryEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LiveScanHistoryEntry>> GetRecentAsync(CancellationToken cancellationToken = default) => Task.FromResult(entries);
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
}
