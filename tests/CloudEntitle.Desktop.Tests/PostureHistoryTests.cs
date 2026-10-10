using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class PostureHistoryTests
{
    [Fact]
    public async Task LiveScan_ShouldPersistTrendAndDeliverNewPrivilegedAccessAlert()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new LocalLiveScanHistoryStore(Path.Combine(directory, "scans.jsonl"));
        var provider = new MutableAccessProvider();
        var notifier = new RecordingPostureNotifier();
        var scanner = new LiveCloudScanService(new[] { provider }, store, postureNotificationChannel: notifier);

        var baseline = await scanner.ScanAsync();
        Assert.Empty(baseline.PostureChanges);
        Assert.Empty(notifier.LastAlerts);

        provider.Access = new[]
        {
            new CloudEffectiveAccessAssignment("user-a", "Owner", "/subscriptions/sub-a", "Azure RBAC", true)
        };
        var current = await scanner.ScanAsync();
        var change = Assert.Single(current.PostureChanges);
        Assert.Equal(PostureChangeType.Added, change.Type);
        Assert.True(change.AlertEligible);
        Assert.Equal(1, current.PostureTrend?.Added);
        Assert.Single(notifier.LastAlerts);
        Assert.Equal("Delivered 1 posture alert(s).", current.NotificationDelivery);

        var saved = await store.GetRecentAsync();
        Assert.Equal(2, saved.Count);
        Assert.Equal(1, saved[0].PostureTrend?.EffectiveAccess);
        Assert.Single(saved[0].PostureChanges);
        Assert.Equal("Delivered 1 posture alert(s).", saved[0].NotificationDelivery);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task LiveScan_ShouldRetryPendingPostureAlertAfterDeliveryFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new LocalLiveScanHistoryStore(Path.Combine(directory, "scans.jsonl"));
        var provider = new MutableAccessProvider();
        var notifier = new RecordingPostureNotifier { IsConfigured = false };
        var scanner = new LiveCloudScanService(new[] { provider }, store, postureNotificationChannel: notifier);

        await scanner.ScanAsync();
        provider.Access = new[] { new CloudEffectiveAccessAssignment("user-a", "Owner", "/subscriptions/sub-a", "Azure RBAC", true) };
        var firstAlertScan = await scanner.ScanAsync();
        var historyAfterFailedDelivery = await store.GetRecentAsync();
        Assert.Single(historyAfterFailedDelivery[0].PendingNotifications);
        Assert.Single(firstAlertScan.PostureChanges, change => change.AlertEligible);
        Assert.Contains("not configured", firstAlertScan.NotificationDelivery, StringComparison.OrdinalIgnoreCase);

        notifier.IsConfigured = true;
        var retryScan = await scanner.ScanAsync();
        var latest = (await store.GetRecentAsync()).First();

        Assert.Empty(retryScan.PostureChanges);
        Assert.Single(notifier.LastAlerts);
        Assert.Empty(latest.PendingNotifications);
        Assert.Contains("Delivered 1", latest.NotificationDelivery, StringComparison.Ordinal);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task LiveScan_ShouldReportStartedAndCompletedDataSources()
    {
        var progress = new RecordingProgress<LiveScanProgress>();
        var scanner = new LiveCloudScanService(new[] { new MutableAccessProvider() });

        await scanner.ScanAsync(null, progress);

        Assert.Contains(progress.Values, update => update.DataSource == "Cloud accounts" && update.State == LiveScanProgressState.Started);
        Assert.Contains(progress.Values, update => update.DataSource == "Cloud accounts" && update.State == LiveScanProgressState.Completed && update.RecordCount == 1);
        Assert.Contains(progress.Values, update => update.DataSource == "Effective access" && update.State == LiveScanProgressState.Completed);
    }

    [Fact]
    public async Task ConcurrentManualAndScheduledScans_ShouldRunSerially()
    {
        var provider = new BlockingDiscoveryProvider();
        var scanner = new LiveCloudScanService(new[] { provider });

        var firstScan = scanner.ScanAsync();
        await provider.FirstDiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondScan = scanner.ScanAsync();
        Assert.False(provider.SecondDiscoveryEntered.Task.IsCompleted);
        provider.ReleaseDiscovery();

        await Task.WhenAll(firstScan, secondScan);

        Assert.Equal(2, provider.DiscoveryCount);
        Assert.Equal(1, provider.MaxConcurrentDiscovery);
    }

    [Fact]
    public async Task LiveScan_ShouldHandleLegacyHistoryWithNullPostureCollections()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var historyPath = Path.Combine(directory, "scans.jsonl");
        var now = DateTime.UtcNow.AddMinutes(-2);
        var legacy = new LiveScanHistoryEntry(now.AddMinutes(-1), now, "Azure", 1, 0, 0, 0, 0, 0, null!, null!)
        {
            Coverage = null!,
            WorkloadTraces = null!,
            RiskFindingDetails = null!,
            PostureChanges = null!,
            PendingNotifications = null!,
            NotificationDelivery = null!
        };
        await File.WriteAllTextAsync(historyPath, JsonSerializer.Serialize(legacy));
        var store = new LocalLiveScanHistoryStore(historyPath);
        var scanner = new LiveCloudScanService(new[] { new MutableAccessProvider() }, store, postureNotificationChannel: new RecordingPostureNotifier());

        var result = await scanner.ScanAsync();
        var history = await store.GetRecentAsync();
        Assert.Equal(2, history.Count);
        var latest = history[0];

        Assert.NotNull(result.PostureTrend);
        Assert.Empty(history[1].RiskFindingDetails);
        Assert.Empty(history[1].PendingNotifications);
        Assert.NotNull(latest.AccountDetails);
        Assert.NotNull(result.NotificationDelivery);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void Analyze_ShouldNotReportRemovalsWhenCurrentCoverageIsIncomplete()
    {
        var service = new PostureHistoryService();
        var now = DateTime.UtcNow;
        var access = new LiveEffectiveAccessRecord("Azure", "user-a", "Reader", "scope", "RBAC", false, AccountId: "sub-a");
        var previous = CreateEntry(now.AddMinutes(-1), new[] { access }, ScanCoverageStatus.Collected);
        var current = CreateEntry(now, Array.Empty<LiveEffectiveAccessRecord>(), ScanCoverageStatus.Denied);

        var result = service.Analyze(previous, current);

        Assert.Empty(result.Changes);
        Assert.Equal(0, result.Trend.Removed);
    }

    [Fact]
    public void Analyze_ShouldAlertWhenExistingFindingEscalatesToCritical()
    {
        var service = new PostureHistoryService();
        var now = DateTime.UtcNow;
        var medium = new LiveRiskFindingSnapshot("Azure", "finding-key", "Medium", "Broad role", "owner", "vault", "Review role scope");
        var previous = CreateEntry(now.AddMinutes(-1), Array.Empty<LiveEffectiveAccessRecord>(), ScanCoverageStatus.NoData) with { RiskFindingDetails = new[] { medium } };
        var current = CreateEntry(now, Array.Empty<LiveEffectiveAccessRecord>(), ScanCoverageStatus.NoData) with { RiskFindingDetails = new[] { medium with { Severity = "Critical" } } };

        var change = Assert.Single(service.Analyze(previous, current).Changes);

        Assert.Equal(PostureChangeType.Changed, change.Type);
        Assert.True(change.AlertEligible);
        Assert.Equal("Medium", change.PreviousState);
        Assert.Equal("Critical", change.CurrentState);
    }

    [Fact]
    public async Task ScanHistory_ShouldShowPostureTrendDriftAndDeliveryStatus()
    {
        var now = DateTime.UtcNow;
        var change = CreateAlert(now);
        var trend = new PostureTrendRecord(now, "Azure", 1, 3, 4, 2, 5, 1, 1, 0, 3, 1, 0, 0);
        var entry = new LiveScanHistoryEntry(now.AddMinutes(-1), now, "Azure", 1, 3, 4, 2, 5, 1, Array.Empty<LiveEffectiveAccessRecord>(), Array.Empty<LiveScanAccountRecord>())
        {
            PostureTrend = trend,
            PostureChanges = new[] { change },
            NotificationDelivery = "Delivered 1 posture alert(s)."
        };
        var viewModel = new ScanHistoryViewModel(new EmptyDemoDataService(), liveScanHistoryStore: new FixedHistoryStore(new[] { entry }));

        await viewModel.RefreshAsync();

        Assert.Equal(1, Assert.Single(viewModel.PostureTrends).Added);
        Assert.Equal("EffectiveAccess", Assert.Single(viewModel.DriftAlerts).EntityType);
        Assert.Equal("Delivered 1 posture alert(s).", viewModel.NotificationDeliveryMessage);
    }

    [Fact]
    public async Task WebhookChannel_ShouldDeliverJsonWithoutExposingConfiguredUrlInStatus()
    {
        var handler = new RecordingHttpHandler();
        using var client = new HttpClient(handler);
        const string webhookUrl = "https://hooks.example.invalid/private/secret";
        var channel = new WebhookPostureNotificationChannel(client, () => webhookUrl);
        var alert = CreateAlert(DateTime.UtcNow);

        var result = await channel.DeliverAsync(new[] { alert });

        Assert.Equal(1, result.Delivered);
        Assert.Equal(HttpMethod.Post, handler.RequestMethod);
        Assert.Equal("https://hooks.example.invalid/private/secret", handler.RequestUri?.ToString());
        Assert.Contains("YASK-CIEM", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("private/secret", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebhookChannel_ShouldRejectNonHttpsEndpointWithoutSending()
    {
        var handler = new RecordingHttpHandler();
        using var client = new HttpClient(handler);
        var channel = new WebhookPostureNotificationChannel(client, () => "http://hooks.example.invalid/insecure");

        var result = await channel.DeliverAsync(new[] { CreateAlert(DateTime.UtcNow) });

        Assert.Equal(0, result.Delivered);
        Assert.Null(handler.RequestUri);
        Assert.Contains("HTTPS", result.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("insecure", result.Status, StringComparison.Ordinal);
    }

    private static LiveScanHistoryEntry CreateEntry(DateTime completedAt, IReadOnlyList<LiveEffectiveAccessRecord> access, ScanCoverageStatus status)
        => new(completedAt.AddMinutes(-1), completedAt, "Azure", 1, 0, 0, 0, 0, 0, access, Array.Empty<LiveScanAccountRecord>())
        {
            Coverage = new[] { new ScanCoverageRecord("Azure", "sub-a: Effective access", status, access.Count, status.ToString()) }
        };

    private static PostureChangeRecord CreateAlert(DateTime detectedAt)
        => new("Azure", PostureChangeType.Added, "EffectiveAccess", "access-key", "High", "New privileged access observed.", "Not observed", "Owner", detectedAt, true);

    private sealed class RecordingPostureNotifier : IPostureNotificationChannel
    {
        public bool IsConfigured { get; set; } = true;
        public IReadOnlyList<PostureChangeRecord> LastAlerts { get; private set; } = Array.Empty<PostureChangeRecord>();
        public Task<PostureNotificationResult> DeliverAsync(IReadOnlyList<PostureChangeRecord> alerts, CancellationToken cancellationToken = default)
        {
            LastAlerts = alerts;
            return Task.FromResult(IsConfigured
                ? new PostureNotificationResult(true, alerts.Count, $"Delivered {alerts.Count} posture alert(s).")
                : new PostureNotificationResult(false, 0, "Webhook not configured; alert delivery skipped."));
        }
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public HttpMethod? RequestMethod { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestMethod = request.Method;
            RequestUri = request.RequestUri;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(Body);
            Assert.True(document.RootElement.TryGetProperty("alerts", out var alerts));
            Assert.Single(alerts.EnumerateArray());
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = new();
        public void Report(T value) => Values.Add(value);
    }

    private sealed class MutableAccessProvider : ICloudProvider, ICloudEffectiveAccessProvider
    {
        private readonly Guid _accountId = Guid.NewGuid();
        public IReadOnlyList<CloudEffectiveAccessAssignment> Access { get; set; } = Array.Empty<CloudEffectiveAccessAssignment>();
        public CloudProviderType ProviderType => CloudProviderType.Azure;
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudAccount>>(new[] { new CloudAccount { Id = _accountId, Name = "posture-test", ExternalAccountId = "sub-a", CloudProvider = CloudProviderType.Azure } });
        public Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudPrincipal>>(Array.Empty<CloudPrincipal>());
        public Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudRole>>(Array.Empty<CloudRole>());
        public Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudPermission>>(Array.Empty<CloudPermission>());
        public Task<IReadOnlyList<CloudEffectiveAccessAssignment>> DiscoverEffectiveAccessAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(Access);
    }

    private sealed class BlockingDiscoveryProvider : ICloudProvider
    {
        private readonly Guid _accountId = Guid.NewGuid();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeDiscoveries;
        private int _discoveryCount;

        public TaskCompletionSource FirstDiscoveryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondDiscoveryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DiscoveryCount => Volatile.Read(ref _discoveryCount);
        public int MaxConcurrentDiscovery { get; private set; }
        public CloudProviderType ProviderType => CloudProviderType.Azure;
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public async Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _discoveryCount);
            var active = Interlocked.Increment(ref _activeDiscoveries);
            MaxConcurrentDiscovery = Math.Max(MaxConcurrentDiscovery, active);
            try
            {
                if (call == 1)
                {
                    FirstDiscoveryEntered.TrySetResult();
                    await _release.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    SecondDiscoveryEntered.TrySetResult();
                }
                return new[] { new CloudAccount { Id = _accountId, Name = "serial-test", ExternalAccountId = "sub-serial", CloudProvider = ProviderType } };
            }
            finally
            {
                Interlocked.Decrement(ref _activeDiscoveries);
            }
        }

        public void ReleaseDiscovery() => _release.TrySetResult();

        public Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudPrincipal>>(Array.Empty<CloudPrincipal>());
        public Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudRole>>(Array.Empty<CloudRole>());
        public Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudPermission>>(Array.Empty<CloudPermission>());
    }

    private sealed class EmptyDemoDataService : IDemoDataService
    {
        public event EventHandler? DataChanged { add { } remove { } }
        public bool IsDemoMode => false;
        public Task<DemoStatistics> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DemoStatistics(0, 0, 0, 0, 0, 0, 0, null));
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DemoSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DemoSnapshot(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(), Array.Empty<Entitlement>(), Array.Empty<CloudEntitle.Domain.Entities.EffectiveAccess>(), Array.Empty<RiskFindingModel>(), Array.Empty<RecommendationModel>(), Array.Empty<Scan>()));
    }

    private sealed class FixedHistoryStore(IReadOnlyList<LiveScanHistoryEntry> entries) : ILiveScanHistoryStore
    {
        public Task RecordAsync(LiveScanHistoryEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LiveScanHistoryEntry>> GetRecentAsync(CancellationToken cancellationToken = default) => Task.FromResult(entries);
    }
}
