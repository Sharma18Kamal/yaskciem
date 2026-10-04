using System.IO;
using System.ComponentModel;
using System.Text.Json;
using CloudEntitle.Desktop.ViewModels;
using Serilog;

namespace CloudEntitle.Desktop.Services;

public sealed record MonitoringSchedule(bool Enabled = false, int IntervalMinutes = 1440);

public interface IMonitoringScheduleStore
{
    Task<MonitoringSchedule> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(MonitoringSchedule schedule, CancellationToken cancellationToken = default);
}

public sealed class LocalMonitoringScheduleStore : IMonitoringScheduleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string _filePath;

    public LocalMonitoringScheduleStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YASK-CIEM",
            "monitoring-schedule.json");
    }

    public async Task<MonitoringSchedule> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return new MonitoringSchedule();
        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            return JsonSerializer.Deserialize<MonitoringSchedule>(json, JsonOptions) ?? new MonitoringSchedule();
        }
        catch (JsonException)
        {
            return new MonitoringSchedule();
        }
    }

    public async Task SaveAsync(MonitoringSchedule schedule, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(schedule, JsonOptions), cancellationToken);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}

public sealed class ContinuousMonitoringService : IDisposable, INotifyPropertyChanged
{
    private const int MinimumIntervalMinutes = 5;
    private const int MaximumIntervalMinutes = 10080;
    private readonly ILiveCloudScanService _scanService;
    private readonly IMonitoringScheduleStore _scheduleStore;
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private bool _initialized;
    private string _status = "Scheduled scans are disabled.";

    public MonitoringSchedule Schedule { get; private set; } = new();
    public string Status => _status;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ContinuousMonitoringService(ILiveCloudScanService scanService, IMonitoringScheduleStore scheduleStore)
    {
        _scanService = scanService;
        _scheduleStore = scheduleStore;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        var schedule = await _scheduleStore.LoadAsync(cancellationToken);
        Schedule = IsValid(schedule) ? schedule : new MonitoringSchedule();
        _initialized = true;
        ConfigureLoop();
    }

    public async Task SetScheduleAsync(MonitoringSchedule schedule, CancellationToken cancellationToken = default)
    {
        if (!IsValid(schedule)) throw new ArgumentOutOfRangeException(nameof(schedule), $"Scan interval must be between {MinimumIntervalMinutes} and {MaximumIntervalMinutes} minutes.");
        await _scheduleStore.SaveAsync(schedule, cancellationToken);
        Schedule = schedule;
        _initialized = true;
        await StopLoopAsync();
        ConfigureLoop();
    }

    public async Task<LiveCloudScanResult?> RunScheduledScanAsync(CancellationToken cancellationToken = default)
    {
        if (!Schedule.Enabled) return null;
        SetStatus("Scheduled scan is running.");
        try
        {
            var result = await _scanService.ScanAsync(cancellationToken);
            SetStatus($"Last scheduled scan completed {result.CompletedAt.ToLocalTime():g}. {result.NotificationDelivery}");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus("Scheduled scan was cancelled.");
            throw;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Scheduled YASK-CIEM scan failed");
            SetStatus($"Scheduled scan failed ({exception.GetType().Name}). See the application log.");
            return null;
        }
    }

    private void ConfigureLoop()
    {
        if (!Schedule.Enabled)
        {
            SetStatus("Scheduled scans are disabled.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Schedule.IntervalMinutes);
        _loopCancellation = new CancellationTokenSource();
        _loopTask = RunLoopAsync(interval, _loopCancellation.Token);
        SetStatus($"Scheduled scans are enabled every {FormatInterval(interval)} while the application is open.");
    }

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RunScheduledScanAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task StopLoopAsync()
    {
        var cancellation = _loopCancellation;
        var task = _loopTask;
        _loopCancellation = null;
        _loopTask = null;
        if (cancellation is null) return;
        await cancellation.CancelAsync();
        if (task is not null)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }
        cancellation.Dispose();
    }

    private void SetStatus(string value)
    {
        _status = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
    }

    private static bool IsValid(MonitoringSchedule schedule)
        => schedule.IntervalMinutes is >= MinimumIntervalMinutes and <= MaximumIntervalMinutes;

    private static string FormatInterval(TimeSpan interval)
        => interval.TotalDays >= 1 ? $"{interval.TotalDays:0} day(s)" : interval.TotalHours >= 1 ? $"{interval.TotalHours:0} hour(s)" : $"{interval.TotalMinutes:0} minute(s)";

    public void Dispose()
    {
        var cancellation = _loopCancellation;
        _loopCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }
}
