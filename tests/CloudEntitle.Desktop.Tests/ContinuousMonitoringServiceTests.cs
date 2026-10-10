using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class ContinuousMonitoringServiceTests
{
    [Fact]
    public async Task Initialize_ShouldKeepDefaultScheduleDisabled()
    {
        var scanner = new FakeLiveScanService();
        using var service = new ContinuousMonitoringService(scanner, new MemoryScheduleStore());

        await service.InitializeAsync();

        Assert.False(service.Schedule.Enabled);
        Assert.Contains("disabled", service.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, scanner.ScanCount);
    }

    [Fact]
    public async Task EnabledSchedule_ShouldPersistAndRunTheNormalScanPipeline()
    {
        var store = new MemoryScheduleStore();
        var scanner = new FakeLiveScanService();
        using var service = new ContinuousMonitoringService(scanner, store);
        await service.InitializeAsync();

        await service.SetScheduleAsync(new MonitoringSchedule(true, 60));
        var result = await service.RunScheduledScanAsync();

        Assert.True((await store.LoadAsync()).Enabled);
        Assert.Equal(60, service.Schedule.IntervalMinutes);
        Assert.Equal(1, scanner.ScanCount);
        Assert.NotNull(result);
        Assert.Contains("Last scheduled scan completed", service.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidInterval_ShouldBeRejectedWithoutPersisting()
    {
        var store = new MemoryScheduleStore();
        using var service = new ContinuousMonitoringService(new FakeLiveScanService(), store);
        await service.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetScheduleAsync(new MonitoringSchedule(true, 1)));

        Assert.False((await store.LoadAsync()).Enabled);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10080)]
    public async Task BoundaryIntervals_ShouldBeAccepted(int intervalMinutes)
    {
        using var service = new ContinuousMonitoringService(new FakeLiveScanService(), new MemoryScheduleStore());
        await service.InitializeAsync();

        await service.SetScheduleAsync(new MonitoringSchedule(true, intervalMinutes));

        Assert.Equal(intervalMinutes, service.Schedule.IntervalMinutes);
    }

    private sealed class MemoryScheduleStore : IMonitoringScheduleStore
    {
        public MonitoringSchedule Schedule { get; private set; } = new();
        public Task<MonitoringSchedule> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Schedule);
        public Task SaveAsync(MonitoringSchedule schedule, CancellationToken cancellationToken = default)
        {
            Schedule = schedule;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLiveScanService : ILiveCloudScanService
    {
        public LiveCloudScanResult? CurrentResult { get; private set; }
        public int ScanCount { get; private set; }
        public event EventHandler? ScanCompleted;

        public Task<IReadOnlyList<LiveScanAccountRecord>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LiveScanAccountRecord>>(Array.Empty<LiveScanAccountRecord>());

        public Task<LiveCloudScanResult> ScanAsync(CancellationToken cancellationToken = default)
            => ScanAsync(null, cancellationToken);

        public Task<LiveCloudScanResult> ScanAsync(IReadOnlySet<string>? selectedAccountKeys, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanCount++;
            var result = new LiveCloudScanResult(0, 0, 0, 0, 0, 0, Array.Empty<RiskFinding>(), DateTime.UtcNow,
                Array.Empty<string>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(), Array.Empty<DiscoveredCloudRole>(),
                Array.Empty<DiscoveredCloudPermission>(), DateTime.UtcNow, Array.Empty<LiveEffectiveAccessRecord>(), Array.Empty<LiveScanAccountRecord>());
            CurrentResult = result;
            ScanCompleted?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(result);
        }
    }
}
